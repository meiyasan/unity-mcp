"""
Defines the send_input tool: mouse and keyboard input sent to one Unity editor window as Unity events.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description=(
        "Send mouse or keyboard input to one Unity editor window (Scene, Game, Inspector, a package's window) "
        "as Unity events, not through the operating system: it works while Unity is behind other applications, "
        "does not move the user's pointer and cannot reach another window. Positions are in points from the "
        "top-left corner of the window as a capture of it shows (capture pixels divided by the result's "
        "pixels_per_point and the capture's scale). A game in Play that reads Input or the Input System does "
        "not see these events. Use action='list' to see the open windows."
    ),
    annotations=ToolAnnotations(
        title="Send Input",
        destructiveHint=True,
    ),
)
async def send_input(
    ctx: Context,
    action: Annotated[Literal["click", "double_click", "move", "drag", "scroll", "key", "text", "list"],
                      "What to send; 'list' returns the open windows and sends nothing"] = "click",
    window: Annotated[str, "The window: its type name (SceneView, GameView, InspectorWindow) or part of its title"] | None = None,
    x: Annotated[float, "Points from the window's left edge"] | None = None,
    y: Annotated[float, "Points from the window's top edge (a docked window's first 24 are its tab strip)"] | None = None,
    to_x: Annotated[float, "drag: where it ends, points from the left"] | None = None,
    to_y: Annotated[float, "drag: where it ends, points from the top"] | None = None,
    steps: Annotated[int, "drag: number of intermediate moves (default 8)"] | None = None,
    button: Annotated[int, "Mouse button: 0 left (default), 1 right, 2 middle"] | None = None,
    delta_x: Annotated[float, "scroll: horizontal amount"] | None = None,
    delta_y: Annotated[float, "scroll: vertical amount, positive scrolls down"] | None = None,
    key: Annotated[str, "key: a Unity KeyCode name (Return, Escape, Tab, A, F5, LeftArrow)"] | None = None,
    text: Annotated[str, "text: characters typed into the window's focused field"] | None = None,
    modifiers: Annotated[str, "Held keys, joined with '+': shift, ctrl, alt, cmd"] | None = None,
    allow_tab_strip: Annotated[bool, "Allow a position on a docked window's tab strip (changes tabs or drags one)"] | None = None,
) -> MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)
    params: dict[str, Any] = {
        "action": action, "window": window, "x": x, "y": y, "to_x": to_x, "to_y": to_y, "steps": steps,
        "button": button, "delta_x": delta_x, "delta_y": delta_y, "key": key, "text": text,
        "modifiers": modifiers, "allow_tab_strip": allow_tab_strip,
    }
    params = {k: v for k, v in params.items() if v is not None}
    result = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "send_input", params)
    return MCPResponse(**result) if isinstance(result, dict) else result
