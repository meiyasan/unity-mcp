"""Timeouts on custom tools must be long-budget, loud, and impossible to misread.

Real-world failure (2026-08-11): a long-running project tool (image comparison)
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
    definition = ToolDefinitionModel(name="long_running_tool", description="long tool")
    service._register_tool("proj-1", definition)

    seen_params: dict = {}

    async def fake_send(_send_fn, _instance, _command, params, **kwargs):  # noqa: ARG001
        seen_params.update(params)
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.custom_tool_service.send_with_unity_instance", fake_send)

    result = await service.execute_tool("proj-1", "long_running_tool", "Playground@abc", {})
    assert result.success is True
    assert seen_params["timeout_seconds"] == 300

    # An explicit caller value must win over the default.
    seen_params.clear()
    await service.execute_tool(
        "proj-1", "long_running_tool", "Playground@abc", {"timeout_seconds": 900})
    assert seen_params["timeout_seconds"] == 900

    # So must the camelCase spelling the hub also honors: no snake_case default beside it.
    seen_params.clear()
    await service.execute_tool(
        "proj-1", "long_running_tool", "Playground@abc", {"timeoutSeconds": 1200})
    assert seen_params == {"timeoutSeconds": 1200}


@pytest.mark.asyncio
async def test_polling_deadline_does_not_suggest_resubmitting(monkeypatch):
    """At the polling deadline the operation may still run: say so, and do not hint a retry."""
    service = _make_service()

    async def still_running(_send_fn, _instance, _command, params, **kwargs):  # noqa: ARG001
        return {"_mcp_status": "pending", "message": "working"}

    monkeypatch.setattr(
        "services.custom_tool_service.send_with_unity_instance", still_running)

    result = await service._poll_until_complete(
        "long_running_tool", "Playground@abc", {}, {"_mcp_status": "pending"}, "status",
        max_poll_seconds=0.01)

    assert result.success is False
    assert "may still be running" in result.message
    assert result.hint is None


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
        unused_send_fn, "Playground@abc", "long_running_tool", {})

    assert response["success"] is False
    assert "may or may not have executed" in response["error"]
    assert "timeout_seconds" in response["error"]
    assert response["hint"] == "retry"
    assert response["data"] == {"reason": "unity_timeout"}


@pytest.mark.asyncio
async def test_each_poll_waits_no_longer_than_the_remaining_budget(monkeypatch):
    """A poll must not inherit the tool's 300 s command budget past the polling deadline."""
    service = _make_service()
    seen: list[dict] = []

    async def complete(_send_fn, _instance, _command, params, **kwargs):  # noqa: ARG001
        seen.append(dict(params))
        return {"_mcp_status": "complete", "success": True, "message": "done"}

    monkeypatch.setattr(
        "services.custom_tool_service.send_with_unity_instance", complete)

    result = await service._poll_until_complete(
        "long_running_tool", "Playground@abc", {"timeout_seconds": 300, "timeoutSeconds": 300},
        {"_mcp_status": "pending", "_mcp_poll_interval": 0.01}, "status", max_poll_seconds=5)

    assert result.success is True
    assert seen and 0 < seen[0]["timeout_seconds"] <= 5
    assert "timeoutSeconds" not in seen[0]
    assert seen[0]["action"] == "status"


@pytest.mark.asyncio
async def test_a_hung_poll_is_cut_off_at_the_polling_deadline(monkeypatch):
    """The hub waits at least 30 s for any command; a poll must still end at the polling deadline."""
    service = _make_service()

    async def hang(_send_fn, _instance, _command, _params, **kwargs):  # noqa: ARG001
        await asyncio.sleep(30)

    monkeypatch.setattr(
        "services.custom_tool_service.send_with_unity_instance", hang)

    loop = asyncio.get_running_loop()
    started = loop.time()
    result = await service._poll_until_complete(
        "long_running_tool", "Playground@abc", {},
        {"_mcp_status": "pending", "_mcp_poll_interval": 0.01}, "status", max_poll_seconds=1)
    elapsed = loop.time() - started

    assert result.success is False
    assert "Timeout waiting for long_running_tool" in result.message
    assert elapsed < 2.0


@pytest.mark.asyncio
async def test_a_stdio_poll_stops_its_worker_thread_at_the_polling_deadline(monkeypatch):
    """On stdio the poll runs in an executor thread that a cancelled wait cannot stop. It must be given the
    remaining budget, so the thread itself ends at the polling deadline and releases the connection."""
    import time

    import transport.legacy.unity_connection as unity_connection

    calls: list[dict] = []

    class _BlockingConnection:
        def send_command(self, command_type, params, max_attempts=None, deadline=None):  # noqa: ARG002
            call = {"deadline": deadline, "ended": None}
            calls.append(call)
            started = time.monotonic()
            # Block like a wedged socket would, until the deadline the caller allows (5 s at most here).
            time.sleep(min(5.0, max(0.0, (deadline or started + 5.0) - started)))
            call["ended"] = time.monotonic()
            return {"_mcp_status": "pending", "_mcp_poll_interval": 0.01}

    monkeypatch.setattr(config, "transport_mode", "stdio")
    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda instance_id=None: _BlockingConnection())

    service = _make_service()
    started = time.monotonic()
    result = await service._poll_until_complete(
        "long_running_tool", None, {},
        {"_mcp_status": "pending", "_mcp_poll_interval": 0.01}, "status", max_poll_seconds=1)
    await asyncio.sleep(0.2)  # let a thread still blocked past the deadline (the bug) record itself

    assert result.success is False
    assert calls, "the poll must have reached the stdio connection"
    assert all(c["deadline"] is not None and c["deadline"] <= started + 1.5 for c in calls)
    assert all(c["ended"] is not None and c["ended"] <= started + 1.5 for c in calls), "a poll thread outlived the deadline"


def test_budget_only_shortens_the_configured_command_deadline(monkeypatch):
    import time

    import transport.legacy.unity_connection as unity_connection

    deadlines: list[float] = []

    class _Connection:
        def send_command(self, command_type, params, max_attempts=None, deadline=None):  # noqa: ARG002
            deadlines.append(deadline - time.monotonic())
            return {"success": True}

    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda instance_id=None: _Connection())
    monkeypatch.setattr(config, "command_total_timeout", 90.0)

    unity_connection.send_command_with_retry("x", {}, budget_s=2.0)
    unity_connection.send_command_with_retry("x", {}, budget_s=500.0)
    unity_connection.send_command_with_retry("x", {})

    assert deadlines[0] <= 2.0
    assert 85.0 < deadlines[1] <= 90.0
    assert 85.0 < deadlines[2] <= 90.0
