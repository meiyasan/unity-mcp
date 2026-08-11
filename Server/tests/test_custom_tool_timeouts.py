"""Timeouts on custom tools must be long-budget, loud, and impossible to misread.

Real-world failure (2026-08-11): a long-running project tool (ground-truth grading)
exceeded the hub's default 30s command budget and the caller got the five words
{"success":false,"error":"TimeoutError","hint":null} — after which the agent
carried on reading stale on-disk artifacts as if the tool had run. Three fixes:
custom tools default to a 300s budget, _normalize_response no longer drops the
hint, and transport-level timeouts explain that the command may not have run.
"""

import asyncio

import pytest

from core.config import config
from models.models import MCPResponse, ToolDefinitionModel
from services.custom_tool_service import CustomToolService


class _NoopMcp:
    def custom_route(self, _path, methods=None):  # noqa: ARG002
        def _decorator(fn):
            return fn

        return _decorator

    def tool(self, name=None, description=None):  # noqa: ARG002
        def _decorator(fn):
            return fn

        return _decorator


def _make_service() -> CustomToolService:
    return CustomToolService(_NoopMcp(), project_scoped_tools=True)


def test_normalize_response_preserves_hint():
    service = _make_service()
    result = service._normalize_response(
        {"success": False, "error": "boom", "hint": "retry", "data": {"reason": "x"}}
    )
    assert isinstance(result, MCPResponse)
    assert result.hint == "retry"
    assert result.data == {"reason": "x"}


@pytest.mark.asyncio
async def test_execute_tool_defaults_long_timeout(monkeypatch):
    service = _make_service()
    definition = ToolDefinitionModel(name="flash_ground_truth", description="long tool")
    service._register_tool("proj-1", definition)

    seen_params: dict = {}

    async def fake_send(_send_fn, _instance, _command, params, **kwargs):  # noqa: ARG001
        seen_params.update(params)
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.custom_tool_service.send_with_unity_instance", fake_send)

    result = await service.execute_tool("proj-1", "flash_ground_truth", "Playground@abc", {})
    assert result.success is True
    assert seen_params["timeout_seconds"] == 300

    # An explicit caller value must win over the default.
    seen_params.clear()
    await service.execute_tool(
        "proj-1", "flash_ground_truth", "Playground@abc", {"timeout_seconds": 900})
    assert seen_params["timeout_seconds"] == 900


@pytest.mark.asyncio
async def test_transport_timeout_error_is_self_explanatory(monkeypatch):
    from transport import unity_transport

    monkeypatch.setattr(config, "transport_mode", "http")

    async def raise_timeout(*args, **kwargs):  # noqa: ARG001
        raise asyncio.TimeoutError()

    monkeypatch.setattr(
        unity_transport.PluginHub, "send_command_for_instance", raise_timeout)

    async def unused_send_fn(*args, **kwargs):  # noqa: ARG001
        raise AssertionError("stdio path should not be used in http mode")

    response = await unity_transport.send_with_unity_instance(
        unused_send_fn, "Playground@abc", "flash_ground_truth", {})

    assert response["success"] is False
    assert "may or may not have executed" in response["error"]
    assert "timeout_seconds" in response["error"]
    assert response["hint"] == "retry"
    assert response["data"] == {"reason": "unity_timeout"}
