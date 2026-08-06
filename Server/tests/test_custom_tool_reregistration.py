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


def test_optional_parameter_declared_first_does_not_break_registration():
    """A tool may declare its optional parameters before its required ones.

    Python forbids a non-default argument after a defaulted one, and the incoming order is whatever the
    plugin declared — C# reflection order in the Unity package, which is not guaranteed. Building the
    signature in that order raised ValueError, which escaped register_global_tools and took every OTHER
    custom tool in the batch with it: a real plugin registered 40 tools and none of them appeared.
    """
    mcp = _RecordingMcp()
    service = CustomToolService(mcp, project_scoped_tools=False)

    definition = ToolDefinitionModel(
        name="awkward_tool",
        description="optional first, required second",
        parameters=[
            ToolParameterModel(name="action", description="optional", required=False, default_value="audit"),
            ToolParameterModel(name="name", description="required", required=True),
        ],
    )
    service._register_global_tool(definition)

    assert mcp.added == ["awkward_tool"]
    sig = service._build_signature(definition)
    names = [p for p in sig.parameters if p != "ctx"]
    assert names == ["name", "action"], "required parameters must be ordered first"


def test_one_bad_tool_does_not_block_the_rest_of_the_batch():
    """Registration is per-tool. A tool that cannot be registered is skipped and named, not fatal."""
    mcp = _RecordingMcp()
    service = CustomToolService(mcp, project_scoped_tools=False)

    exploding = ToolDefinitionModel(name="bad_tool", description="boom")
    good = ToolDefinitionModel(name="good_tool", description="fine")

    original = service._build_global_tool_handler

    def _sometimes_explode(definition):
        if definition.name == "bad_tool":
            raise ValueError("simulated registration failure")
        return original(definition)

    service._build_global_tool_handler = _sometimes_explode
    service.register_global_tools([exploding, good])

    assert mcp.added == ["good_tool"], "a failing tool must not stop the ones after it"
    assert "good_tool" in service._global_tools
    assert "bad_tool" not in service._global_tools


def test_wrapped_handler_keeps_its_declared_parameters():
    """The object handed to FastMCP must carry the signature ITSELF, not via ``__wrapped__``.

    ``_build_global_tool_handler`` stamps a synthetic signature, then two ``functools.wraps`` decorators wrap
    it. ``inspect.signature`` follows ``__wrapped__`` and reports the right thing, so this looked correct from
    every angle — but FastMCP builds its schema with pydantic's TypeAdapter, which reads the WRAPPER's own
    (empty) ``__annotations__`` and raises ``KeyError`` on the first parameter name. Every custom tool with
    parameters was silently dropped.
    """
    import inspect as _inspect
    from services.custom_tool_service import CustomToolService as _S

    mcp = _RecordingMcp()
    service = _S(mcp, project_scoped_tools=False)
    definition = _definition(["name", "frame"])

    captured = {}
    real_tool = mcp.tool

    def _capture(name=None, description=None):
        def _decorator(fn):
            captured["fn"] = fn
            return real_tool(name=name, description=description)(fn)
        return _decorator

    mcp.tool = _capture
    service._register_global_tool(definition)

    fn = captured["fn"]
    assert not hasattr(fn, "__wrapped__"), "__wrapped__ must not point at a differently-shaped function"
    assert sorted(k for k in fn.__annotations__ if k != "return") == ["ctx", "frame", "name"]
    assert [p for p in _inspect.signature(fn).parameters if p != "ctx"] == ["name", "frame"]
