"""Tests for manage_editor tool."""
import asyncio
import inspect
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_editor import manage_editor
import services.tools.manage_editor as manage_editor_mod
from services.registry import get_registered_tools

# ── Fixture ──────────────────────────────────────────────────────────


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_editor.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor.send_with_unity_instance",
        fake_send,
    )
    return captured


# ── Undo/Redo ────────────────────────────────────────────────────────


def test_undo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "undo"
    assert mock_unity["tool_name"] == "manage_editor"


def test_redo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="redo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "redo"


# ── All Unity-forwarded actions ──────────────────────────────────────

UNITY_FORWARDED_ACTIONS = [
    "play", "pause", "stop", "set_active_tool",
    "add_tag", "remove_tag", "add_layer", "remove_layer",
    "deploy_package", "restore_package",
    "undo", "redo",
]


@pytest.mark.parametrize("action_name", UNITY_FORWARDED_ACTIONS)
def test_every_action_forwards_to_unity(mock_unity, action_name):
    result = asyncio.run(manage_editor(SimpleNamespace(), action=action_name))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == action_name


# ── Python-only actions ──────────────────────────────────────────────


def test_telemetry_status_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_status"))
    assert result["success"] is True
    assert "telemetry_enabled" in result
    assert "params" not in mock_unity


def test_telemetry_ping_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_ping"))
    assert result["success"] is True
    assert "params" not in mock_unity


# ── None params omitted ─────────────────────────────────────────────


def test_undo_omits_none_params(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    params = mock_unity["params"]
    assert "toolName" not in params
    assert "tagName" not in params
    assert "layerName" not in params




# ── Play mode driven from the background, compile status ─────────────


def test_step_forwards_its_frames(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="step", frames=60))
    assert result["success"] is True
    assert mock_unity["params"] == {"action": "step", "frames": 60}


def test_play_status_and_compile_status_send_nothing_else(mock_unity):
    asyncio.run(manage_editor(SimpleNamespace(), action="play_status"))
    assert mock_unity["params"] == {"action": "play_status"}
    asyncio.run(manage_editor(SimpleNamespace(), action="compile_status", include_warnings=True))
    assert mock_unity["params"] == {"action": "compile_status", "includeWarnings": True}


def test_run_in_background_forwards_enabled(mock_unity):
    asyncio.run(manage_editor(SimpleNamespace(), action="run_in_background", enabled=False))
    assert mock_unity["params"] == {"action": "run_in_background", "enabled": False}


def test_refresh_reports_a_failed_compilation(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        assert tool_name == "manage_editor" and params == {"action": "compile_status"}
        return {"success": True, "data": {"is_compiling": False, "compilation_failed": True, "last_compilation": {
            "failed": True, "error_count": 1, "warning_count": 0,
            "errors": [{"file": "Assets/A.cs", "line": 3, "message": "error CS1002: ; expected"}]}}}

    monkeypatch.setattr(refresh_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)
    out = {"success": True, "message": "Refresh complete; editor is ready.", "data": {}}
    asyncio.run(refresh_mod._attach_compilation(out, "unity-instance-1"))
    assert out["data"]["compilation"]["failed"] is True
    assert "FAILED to compile" in out["message"] and "Assets/A.cs(3)" in out["message"]
