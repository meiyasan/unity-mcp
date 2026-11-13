"""send_input forwards only the parameters given, under the names the Unity handler reads."""
import pytest

import services.tools.send_input as mod


async def _async_none(ctx):
    return None


def _capture(monkeypatch):
    sent: dict = {}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        sent.update(command=command_type, params=params)
        return {"success": True, "message": "Sent 2 event(s) to 'Scene'.", "data": {"events": ["MouseDown (10, 40)", "MouseUp (10, 40)"]}}

    monkeypatch.setattr(mod, "send_with_unity_instance", fake_send)
    monkeypatch.setattr(mod, "get_unity_instance_from_context", _async_none)
    return sent


@pytest.mark.asyncio
async def test_a_click_sends_its_window_and_position_and_nothing_unset(monkeypatch):
    sent = _capture(monkeypatch)

    response = await mod.send_input(object(), action="click", window="SceneView", x=10, y=40)

    assert response.success
    assert sent["command"] == "send_input"
    assert sent["params"] == {"action": "click", "window": "SceneView", "x": 10, "y": 40}


@pytest.mark.asyncio
async def test_a_drag_keeps_zero_valued_and_false_parameters(monkeypatch):
    """0 and False are values, not absences: a drag to the left edge or button 0 must reach Unity."""
    sent = _capture(monkeypatch)

    await mod.send_input(object(), action="drag", window="Game", x=5, y=30, to_x=0, to_y=30,
                         button=0, modifiers="shift", allow_tab_strip=False)

    assert sent["params"] == {"action": "drag", "window": "Game", "x": 5, "y": 30, "to_x": 0, "to_y": 30,
                              "button": 0, "modifiers": "shift", "allow_tab_strip": False}


@pytest.mark.asyncio
async def test_list_needs_no_window(monkeypatch):
    sent = _capture(monkeypatch)

    await mod.send_input(object(), action="list")

    assert sent["params"] == {"action": "list"}
