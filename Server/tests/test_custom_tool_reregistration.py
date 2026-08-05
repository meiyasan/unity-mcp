"""A custom tool that re-registers with a CHANGED SCHEMA must be replaced, not ignored.

Unity re-registers its custom tools on every domain reload, so a tool's signature is re-declared constantly.
The registry used to keep whatever definition arrived first and log "keeping existing definition" for the
rest, which froze the signature at its earliest — and least correct — version: you add a tool, call it,
discover the arguments are wrong, fix them, and the server goes on rejecting the arguments the tool now
declares. Only a server restart cleared it, and nothing in the symptom pointed there.
"""

from unittest.mock import Mock

from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService


class _RecordingMcp:
    """Minimal FastMCP stand-in that records which tools were added and removed."""

    def __init__(self, supports_removal: bool = True):
        self.added: list[str] = []
        self.removed: list[str] = []
        if supports_removal:
            self.remove_tool = self._remove_tool

    def custom_route(self, _path, methods=None):  # noqa: ARG002
        def _decorator(fn):
            return fn

        return _decorator

    def tool(self, name=None, description=None):  # noqa: ARG002
        self.added.append(name)

        def _decorator(fn):
            return fn

        return _decorator

    def _remove_tool(self, name):
        self.removed.append(name)


def _definition(params: list[str]) -> ToolDefinitionModel:
    return ToolDefinitionModel(
        name="my_tool",
        description="a custom tool",
        parameters=[ToolParameterModel(name=p, description=p) for p in params],
    )


def test_changed_schema_replaces_the_previous_registration():
    mcp = _RecordingMcp()
    service = CustomToolService(mcp, project_scoped_tools=False)

    service._register_global_tool(_definition([]))            # first sight: no parameters
    service._register_global_tool(_definition(["name"]))      # author adds one

    assert mcp.removed == ["my_tool"], "the stale signature must be removed from FastMCP"
    assert mcp.added == ["my_tool", "my_tool"], "the new signature must be registered"
    assert [p.name for p in service._global_tools["my_tool"].parameters] == ["name"]


def test_identical_schema_is_not_re_registered():
    """Re-registering the SAME definition must stay a no-op — a domain reload happens constantly and must
    not churn the tool registry (or its telemetry wrappers)."""
    mcp = _RecordingMcp()
    service = CustomToolService(mcp, project_scoped_tools=False)

    service._register_global_tool(_definition(["name"]))
    service._register_global_tool(_definition(["name"]))

    assert mcp.removed == []
    assert mcp.added == ["my_tool"]


def test_missing_removal_api_keeps_the_old_tool_rather_than_losing_it():
    """On a FastMCP with no removal API, the old signature survives — degraded but serving. Dropping it from
    the registry while FastMCP still routed to it would be worse than a stale schema."""
    mcp = _RecordingMcp(supports_removal=False)
    service = CustomToolService(mcp, project_scoped_tools=False)

    service._register_global_tool(_definition([]))
    service._register_global_tool(_definition(["name"]))

    assert mcp.added == ["my_tool", "my_tool"]
    assert "my_tool" in service._global_tools
