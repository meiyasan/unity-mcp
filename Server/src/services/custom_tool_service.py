import asyncio
import inspect
import logging
import time
from hashlib import sha256
from typing import Optional

from fastmcp import Context, FastMCP
from pydantic import BaseModel, Field, ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse

from core.config import config
from models.models import MCPResponse, ToolDefinitionModel, ToolParameterModel
from core.logging_decorator import log_execution
from core.telemetry_decorator import telemetry_tool
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import (
    async_send_command_with_retry,
    get_unity_connection_pool,
)
from transport.plugin_hub import PluginHub
from services.tools import get_unity_instance_from_context
from services.registry import get_registered_tools

logger = logging.getLogger("mcp-for-unity-server")

_DEFAULT_POLL_INTERVAL = 1.0
_MAX_POLL_SECONDS = 600


async def get_user_id_from_context(ctx: Context) -> str | None:
    """Read user_id from request-scoped context in remote-hosted mode."""
    if not config.http_remote_hosted:
        return None

    get_state = getattr(ctx, "get_state", None)
    if not callable(get_state):
        return None

    try:
        user_id = await get_state("user_id")
    except Exception:
        return None

    return user_id if isinstance(user_id, str) and user_id else None


class RegisterToolsPayload(BaseModel):
    project_id: str
    project_hash: str | None = None
    tools: list[ToolDefinitionModel]


class ToolRegistrationResponse(BaseModel):
    success: bool
    registered: list[str]
    replaced: list[str]
    message: str


class CustomToolService:
    _instance: "CustomToolService | None" = None

    def __init__(self, mcp: FastMCP, project_scoped_tools: bool = True):
        CustomToolService._instance = self
        self._mcp = mcp
        self._project_scoped_tools = project_scoped_tools
        self._project_tools: dict[str, dict[str, ToolDefinitionModel]] = {}
        self._hash_to_project: dict[str, str] = {}
        self._global_tools: dict[str, ToolDefinitionModel] = {}
        # What each project (owner key) last declared for each global tool. A tool's owners are the projects
        # whose declaration is the definition being served.
        self._global_tool_declarations: dict[str, dict[str, dict]] = {}
        self._register_http_routes()

    @classmethod
    def get_instance(cls) -> "CustomToolService":
        if cls._instance is None:
            raise RuntimeError("CustomToolService has not been initialized")
        return cls._instance

    # --- HTTP Routes -----------------------------------------------------
    def _register_http_routes(self) -> None:
        # The plugin registers custom tools over the hub WebSocket (register_tools message),
        # so this REST route only serves local tooling. A remote-hosted server must not expose
        # it: it carries no API-key check, so any caller could replace tool definitions for
        # every tenant. Mirrors the /api/command gate in main.py.
        if config.http_remote_hosted:
            return

        @self._mcp.custom_route("/register-tools", methods=["POST"])
        async def register_tools(request: Request) -> JSONResponse:
            try:
                payload = RegisterToolsPayload.model_validate(await request.json())
            except ValidationError as exc:
                return JSONResponse({"success": False, "error": exc.errors()}, status_code=400)

            if not payload.project_hash and self._project_owns_global_tools(payload.project_id):
                # Without its hash the request cannot say which of the project's instances it speaks for,
                # so it could neither release that instance's dropped tools nor leave the others' alone.
                return JSONResponse(
                    {
                        "success": False,
                        "error": "project_hash is required: this project's instances own global custom tools",
                    },
                    status_code=409,
                )

            registered, replaced = self._register_project_tools(
                payload.project_id, payload.tools, project_hash=payload.project_hash)

            message = f"Registered {len(registered)} tool(s)"
            if replaced:
                message += f" (replaced: {', '.join(replaced)})"

            response = ToolRegistrationResponse(
                success=True,
                registered=registered,
                replaced=replaced,
                message=message,
            )
            return JSONResponse(response.model_dump())

    # --- Public API for MCP tools ---------------------------------------
    async def list_registered_tools(
        self,
        project_id: str,
        user_id: str | None = None,
    ) -> list[ToolDefinitionModel]:
        legacy = list(self._project_tools.get(project_id, {}).values())
        hub_tools = await PluginHub.get_tools_for_project(project_id, user_id=user_id)
        return legacy + hub_tools

    async def get_tool_definition(
        self,
        project_id: str,
        tool_name: str,
        user_id: str | None = None,
    ) -> ToolDefinitionModel | None:
        tool = self._project_tools.get(project_id, {}).get(tool_name)
        if tool:
            return tool
        tool = self._global_tools.get(tool_name)
        if tool:
            return tool
        return await PluginHub.get_tool_definition(project_id, tool_name, user_id=user_id)

    async def execute_tool(
        self,
        project_id: str,
        tool_name: str,
        unity_instance: str | None,
        params: dict[str, object] | None = None,
        user_id: str | None = None,
    ) -> MCPResponse:
        params = params or {}
        logger.info(
            f"Executing tool '{tool_name}' for project '{project_id}' (instance={unity_instance}) with params: {params}"
        )

        definition = await self.get_tool_definition(project_id, tool_name, user_id=user_id)
        if definition is None:
            return MCPResponse(
                success=False,
                message=f"Tool '{tool_name}' not found for project {project_id}",
            )

        # Custom tools are frequently long-running (captures, image comparison,
        # batch imports), so the hub's default 30s command budget times them out
        # while they are still working. Give them a generous default; callers can
        # still override with an explicit timeout_seconds (or timeoutSeconds) parameter, up to 3600.
        # The hub reads timeout_seconds first, so a default added beside a caller's timeoutSeconds would win.
        if "timeout_seconds" not in params and "timeoutSeconds" not in params:
            params["timeout_seconds"] = 300

        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            tool_name,
            params,
            user_id=user_id,
        )

        if not definition.requires_polling:
            result = self._normalize_response(response)
            logger.info(f"Tool '{tool_name}' immediate response: {result}")
            return result

        result = await self._poll_until_complete(
            tool_name,
            unity_instance,
            params,
            response,
            definition.poll_action or "status",
            user_id=user_id,
            max_poll_seconds=definition.max_poll_seconds or 0,
        )
        logger.info(f"Tool '{tool_name}' polled response: {result}")
        return result

    # --- Internal helpers ------------------------------------------------
    def _is_registered(self, project_id: str, tool_name: str) -> bool:
        return tool_name in self._project_tools.get(project_id, {})

    def _register_tool(self, project_id: str, definition: ToolDefinitionModel) -> None:
        self._project_tools.setdefault(project_id, {})[
            definition.name] = definition

    def get_project_id_for_hash(self, project_hash: str | None) -> str | None:
        if not project_hash:
            return None
        return self._hash_to_project.get(project_hash.lower())

    async def _poll_until_complete(
        self,
        tool_name: str,
        unity_instance,
        initial_params: dict[str, object],
        initial_response,
        poll_action: str,
        user_id: str | None = None,
        max_poll_seconds: int = 0,
    ) -> MCPResponse:
        poll_params = dict(initial_params)
        poll_params["action"] = poll_action or "status"

        timeout = max_poll_seconds if max_poll_seconds > 0 else _MAX_POLL_SECONDS
        deadline = time.time() + timeout
        response = initial_response

        while True:
            status, poll_interval = self._interpret_status(response)

            if status in ("complete", "error", "final"):
                return self._normalize_response(response)

            if time.time() > deadline:
                return MCPResponse(
                    success=False,
                    message=(
                        f"Timeout waiting for {tool_name} to complete. The operation may "
                        "still be running in Unity — do not assume it finished or failed; "
                        "verify its effects before continuing."
                    ),
                    data=self._safe_response(response),
                    # No retry hint: the operation may still be running, and resubmitting it could run it twice.
                )

            await asyncio.sleep(min(poll_interval, max(0.0, deadline - time.time())))

            # A poll inherits the tool's parameters, including its long command budget (300 s by default):
            # left as is, one status poll could wait far past this polling deadline. Give each poll what is
            # left of the budget instead. The hub still waits at least its 30 s default for any command,
            # so the wait itself is also cut off at the deadline here. On stdio the poll runs in a worker
            # thread that a cancelled wait cannot stop, so it is given the same budget (budget_s) and
            # ends by itself, releasing the connection.
            remaining = max(0.0, deadline - time.time())
            if remaining <= 0:
                continue
            poll_params.pop("timeoutSeconds", None)
            poll_params["timeout_seconds"] = max(1.0, remaining)

            try:
                response = await asyncio.wait_for(
                    send_with_unity_instance(
                        async_send_command_with_retry,
                        unity_instance,
                        tool_name,
                        poll_params,
                        user_id=user_id,
                        budget_s=remaining,
                    ),
                    timeout=remaining,
                )
            except asyncio.TimeoutError:
                # The deadline passed mid-poll: keep the last pending response and let the
                # check above report the timeout.
                continue
            except Exception as exc:  # pragma: no cover - network/domain reload variability
                logger.debug(f"Polling {tool_name} failed, will retry: {exc}")
                # Back off modestly but stay responsive.
                response = {
                    "_mcp_status": "pending",
                    "_mcp_poll_interval": min(max(poll_interval * 2, _DEFAULT_POLL_INTERVAL), 5.0),
                    "message": f"Retrying after transient error: {exc}",
                }

    def _interpret_status(self, response) -> tuple[str, float]:
        if response is None:
            return "pending", _DEFAULT_POLL_INTERVAL

        if not isinstance(response, dict):
            return "final", _DEFAULT_POLL_INTERVAL

        status = response.get("_mcp_status")
        if status is None:
            if len(response.keys()) == 0:
                return "pending", _DEFAULT_POLL_INTERVAL
            return "final", _DEFAULT_POLL_INTERVAL

        if status == "pending":
            interval_raw = response.get(
                "_mcp_poll_interval", _DEFAULT_POLL_INTERVAL)
            try:
                interval = float(interval_raw)
            except (TypeError, ValueError):
                interval = _DEFAULT_POLL_INTERVAL

            interval = max(0.1, min(interval, 5.0))
            return "pending", interval

        if status == "complete":
            return "complete", _DEFAULT_POLL_INTERVAL

        if status == "error":
            return "error", _DEFAULT_POLL_INTERVAL

        return "final", _DEFAULT_POLL_INTERVAL

    def _normalize_response(self, response) -> MCPResponse:
        if isinstance(response, MCPResponse):
            return response
        if isinstance(response, dict):
            data = response.get(
                "data", response) if "data" not in response else response["data"]
            hint = response.get("hint")
            if isinstance(data, dict) and data.get("reason") == "unity_timeout":
                # The tool may have run: its effects are to be checked, not the tool resubmitted.
                hint = None
            return MCPResponse(
                success=response.get("success", True),
                message=response.get("message"),
                error=response.get("error"),
                data=data,
                hint=hint,
            )

        success = True
        message = None
        error = None
        data = None

        if isinstance(response, dict):
            success = response.get("success", True)
            if "_mcp_status" in response and response["_mcp_status"] == "error":
                success = False
            message = str(response.get("message")) if response.get(
                "message") else None
            error = str(response.get("error")) if response.get(
                "error") else None
            data = response.get("data")
            if "success" not in response and "_mcp_status" not in response:
                data = response
        else:
            success = False
            message = str(response)

        return MCPResponse(success=success, message=message, error=error, data=data)

    def _safe_response(self, response):
        if isinstance(response, dict):
            return response
        if response is None:
            return None
        return {"message": str(response)}

    def _register_project_tools(
        self,
        project_id: str,
        tools: list[ToolDefinitionModel],
        project_hash: str | None = None,
    ) -> tuple[list[str], list[str]]:
        registered: list[str] = []
        replaced: list[str] = []
        if not self._project_scoped_tools and project_hash:
            self._release_undeclared_tools(project_hash, tools)
        for tool in tools:
            if self._is_registered(project_id, tool.name):
                replaced.append(tool.name)
            self._register_tool(project_id, tool)
            registered.append(tool.name)
            if not self._project_scoped_tools:
                self._register_global_tool(tool, owner=project_hash)

        if project_hash:
            self._hash_to_project[project_hash.lower()] = project_id

        return registered, replaced

    def register_global_tools(
        self,
        tools: list[ToolDefinitionModel],
        owner: str | None = None,
        live_owners: set[str] | None = None,
    ) -> None:
        """Expose a plugin's custom tools through FastMCP.

        ``owner`` is the registering project's hash and ``live_owners`` the hashes of every connected
        project; together they decide whether a changed schema may replace the one being served.
        """
        # Global custom tools are always registered, even when project-scoped tools
        # are enabled. Project-scoped tools can override globals by name, but
        # disabling globals entirely would break shared tooling that projects expect.
        if owner:
            self._release_undeclared_tools(owner, tools, live_owners)
        builtin_names = self._get_builtin_tool_names()
        exposed: list[str] = []
        skipped: list[str] = []
        for tool in tools:
            if tool.name in builtin_names:
                logger.debug(
                    "Skipping global custom tool registration for built-in tool '%s'",
                    tool.name,
                )
                continue
            # One malformed tool must not cost the plugin every other tool it registered. This loop used to
            # let an exception escape to PluginHub, which logged "custom tools may not be available globally"
            # and moved on — leaving the author staring at a plugin whose tools are all missing, with nothing
            # to say which one was at fault.
            try:
                if self._register_global_tool(tool, owner=owner, live_owners=live_owners):
                    exposed.append(tool.name)
                else:
                    skipped.append(tool.name)
            except Exception as exc:
                skipped.append(tool.name)
                logger.warning(
                    "Custom tool '%s' could not be registered and was skipped: %s",
                    tool.name,
                    exc,
                    exc_info=True,
                )

        # SAY WHAT HAPPENED. This path had no success log at all, so "no custom tools appeared" and "no custom
        # tools were offered" were indistinguishable from the outside — and the only failure log was a generic
        # one in PluginHub that named nothing. An operator should be able to answer "did the server expose my
        # tools?" from the log alone.
        if exposed or skipped:
            logger.info(
                "Custom tools exposed globally: %d (%s)%s",
                len(exposed),
                ", ".join(exposed) if exposed else "none",
                f" — skipped {len(skipped)}: {', '.join(skipped)}" if skipped else "",
            )

    @staticmethod
    def _owner_id(owner_key: str) -> str:
        """Normalize an owner key: the project hash ignores case, a user id (``user/hash``) keeps it."""
        user, sep, project_hash = owner_key.rpartition("/")
        return f"{user}{sep}{project_hash.lower()}"

    def _served_owners(self, name: str) -> set[str]:
        """The projects whose declaration of ``name`` is the definition being served."""
        served = self._global_tools.get(name)
        if served is None:
            return set()
        served_dump = served.model_dump()
        return {
            owner
            for owner, declared in self._global_tool_declarations.get(name, {}).items()
            if declared == served_dump
        }

    def _project_owns_global_tools(self, project_id: str) -> bool:
        """Whether an instance of ``project_id`` has declared a global tool. Project-scoped mode tracks none.

        An instance is known by its hash: one mapped to ``project_id`` by an earlier hashed registration, or
        ``project_id`` itself, which is the hash for a project registered through PluginHub.
        """
        if self._project_scoped_tools:
            return False
        hashes = {h for h, pid in self._hash_to_project.items() if pid == project_id}
        hashes.add(project_id.lower())
        return any(
            owner.rpartition("/")[2] in hashes
            for declarations in self._global_tool_declarations.values()
            for owner in declarations
        )

    def _release_undeclared_tools(
        self,
        owner: str,
        tools: list[ToolDefinitionModel],
        live_owners: set[str] | None = None,
    ) -> None:
        """A project's tool list is complete: it no longer declares the tools missing from it.

        Left in place, a stale declaration would count as a connected project serving that schema, and refuse
        another project's changed one. Declarations of projects no longer connected are dropped too.
        """
        owner = self._owner_id(owner)
        declared = {tool.name for tool in tools}
        live = {self._owner_id(key) for key in live_owners} if live_owners is not None else None
        for name, declarations in self._global_tool_declarations.items():
            if name not in declared:
                declarations.pop(owner, None)
            if live is not None:
                for gone in [key for key in declarations if key not in live and key != owner]:
                    del declarations[gone]

    def _get_builtin_tool_names(self) -> set[str]:
        return {tool["name"] for tool in get_registered_tools()}

    def _register_global_tool(
        self,
        definition: ToolDefinitionModel,
        owner: str | None = None,
        live_owners: set[str] | None = None,
    ) -> bool:
        """Expose a custom tool through FastMCP.

        Returns whether FastMCP serves the tool afterwards: this definition, an identical one already there,
        or the previous one, kept because another connected project serves it, because it could not be
        removed, or restored because FastMCP refused this one. False when FastMCP serves neither.
        """
        owner = self._owner_id(owner) if owner else None
        if owner:
            # Recorded whatever is served: a project whose schema is kept out for now owns the tool as soon as
            # its schema is the one served, whoever registers it.
            self._global_tool_declarations.setdefault(definition.name, {})[owner] = definition.model_dump()
        existing = self._global_tools.get(definition.name)
        replaced = None
        if existing:
            if existing.model_dump() == definition.model_dump():
                return True
            # FastMCP serves one schema per tool name, and validates a call against it before the handler
            # routes it to a project. Replacing the definition another connected project declares would make
            # that project's calls fail validation, and two such projects reloading in turn would swap it back
            # and forth; keep it, as before. Projects no longer connected, and projects that now declare
            # another schema, do not hold it: projects sharing a tool that all move to a new schema replace it.
            others = self._served_owners(definition.name) - {owner}
            if live_owners is not None:
                others &= {self._owner_id(key) for key in live_owners}
            if others:
                logger.warning(
                    "Custom tool '%s' from project %s declares a different schema than connected project(s) %s; "
                    "keeping theirs (one schema per tool name is served). Call this project's version through "
                    "execute_custom_tool.",
                    definition.name,
                    owner or "(unknown)",
                    ", ".join(sorted(others)),
                )
                return True
            # Unity re-registers on every domain reload, so the newest definition is authoritative. Keeping the
            # first one froze a tool's signature at its earliest version: after fixing a tool's parameters the
            # server went on rejecting the arguments the tool now declares, until it was restarted.
            logger.info(
                "Custom tool '%s' re-registered with a changed schema — replacing the previous definition.",
                definition.name,
            )
            if not self._unregister_global_tool(definition.name):
                # Registering on top of a tool FastMCP still routes would leave the two out of step; the
                # previous definition keeps serving (warned about in _unregister_global_tool).
                return True
            replaced = existing

        handler = self._build_global_tool_handler(definition)
        wrapped = log_execution(definition.name, "Tool")(handler)
        wrapped = telemetry_tool(definition.name)(wrapped)

        # RE-STAMP THE OUTERMOST CALLABLE. This is what actually kept custom tools from being exposed.
        #
        # The handler is given a synthetic __signature__/__annotations__ so FastMCP can see the plugin's
        # parameters. Both decorators above use functools.wraps, which copies __wrapped__ but leaves the
        # wrapper's OWN __annotations__ empty. inspect.signature follows __wrapped__ and therefore reports the
        # right signature — which is why this looked correct from every angle — but FastMCP builds its schema
        # with pydantic's TypeAdapter, which reads the wrapper's real annotations and raises KeyError on the
        # first parameter name. The tool was then dropped with a one-line warning and no traceback.
        #
        # So: put the signature on the object FastMCP is actually handed, and drop __wrapped__ so nothing
        # resolves back to a function with a different one. Verified against fastmcp 3.0.2 — without this a
        # two-parameter tool fails to register; with it, it registers and exposes both parameters.
        wrapped.__signature__ = self._build_signature(definition)
        wrapped.__annotations__ = self._build_annotations(definition)
        if hasattr(wrapped, "__wrapped__"):
            del wrapped.__wrapped__

        try:
            wrapped = self._mcp.tool(
                name=definition.name,
                description=definition.description,
            )(wrapped)
        except Exception as exc:  # pragma: no cover - defensive against tool conflicts
            logger.warning(
                "Failed to register custom tool '%s' globally: %s",
                definition.name,
                exc,
            )
            if replaced is not None:
                # The previous definition was already removed: put it back rather than lose a working tool.
                return self._register_global_tool(replaced)
            return False

        self._global_tools[definition.name] = definition
        return True

    def _unregister_global_tool(self, name: str) -> bool:
        """Drop a registered custom tool so it can be re-added with a new signature.

        Tries the removal spellings FastMCP has used across the supported range (>=3.0.2,<4). If none exists,
        the old tool keeps serving rather than being dropped from the registry while FastMCP still routes to it.
        Returns whether the tool was removed.
        """
        for attr in ("remove_tool", "unregister_tool", "delete_tool"):
            remover = getattr(self._mcp, attr, None)
            if not callable(remover):
                continue
            try:
                remover(name)
                self._global_tools.pop(name, None)
                return True
            except Exception as exc:  # pragma: no cover - depends on FastMCP version
                logger.debug("FastMCP.%s('%s') failed: %s", attr, name, exc)
        logger.warning(
            "Could not remove custom tool '%s' from FastMCP (no supported removal API); "
            "its signature stays as first registered until the server restarts.",
            name,
        )
        return False

    def _build_global_tool_handler(self, definition: ToolDefinitionModel):
        async def _handler(ctx: Context, **kwargs) -> MCPResponse:
            unity_instance = await get_unity_instance_from_context(ctx)
            if not unity_instance:
                return MCPResponse(
                    success=False,
                    message="No active Unity instance. Call set_active_instance with Name@hash from mcpforunity://instances.",
                )

            project_id = resolve_project_id_for_unity_instance(unity_instance)
            if project_id is None:
                return MCPResponse(
                    success=False,
                    message=f"Could not resolve project id for {unity_instance}. Ensure Unity is running and reachable.",
                )

            params = {k: v for k, v in kwargs.items() if v is not None}
            user_id = await get_user_id_from_context(ctx)
            service = CustomToolService.get_instance()
            return await service.execute_tool(
                project_id,
                definition.name,
                unity_instance,
                params,
                user_id=user_id,
            )

        _handler.__name__ = f"custom_tool_{definition.name}"
        _handler.__doc__ = definition.description or ""
        _handler.__signature__ = self._build_signature(definition)
        _handler.__annotations__ = self._build_annotations(definition)
        return _handler

    def _build_signature(self, definition: ToolDefinitionModel) -> inspect.Signature:
        params: list[inspect.Parameter] = [
            inspect.Parameter(
                "ctx",
                inspect.Parameter.POSITIONAL_OR_KEYWORD,
                annotation=Context,
            )
        ]
        # Required parameters first. Python forbids a non-default argument after a defaulted one, and the
        # incoming order is whatever the plugin happened to declare (in the Unity package that is C# reflection
        # order, which is not even guaranteed). A tool author has no reason to know this rule, and getting it
        # wrong raised ValueError out of register_global_tools and took the WHOLE BATCH of custom tools with
        # it — one tool with its optional argument listed first, and none of the plugin's tools appeared.
        # Sorting is stable, so the author's order survives within each group.
        ordered = sorted(
            (p for p in definition.parameters if p.name.isidentifier()),
            key=lambda p: not p.required,
        )
        for param in definition.parameters:
            if not param.name.isidentifier():
                logger.warning(
                    "Custom tool '%s' has non-identifier parameter '%s'; exposing via kwargs only.",
                    definition.name,
                    param.name,
                )
        for param in ordered:
            default = inspect._empty if param.required else self._coerce_default(
                param.default_value, param.type)
            params.append(
                inspect.Parameter(
                    param.name,
                    inspect.Parameter.POSITIONAL_OR_KEYWORD,
                    default=default,
                    annotation=self._map_param_type(param),
                )
            )
        return inspect.Signature(parameters=params)

    def _build_annotations(self, definition: ToolDefinitionModel) -> dict[str, object]:
        annotations: dict[str, object] = {"ctx": Context}
        for param in definition.parameters:
            if not param.name.isidentifier():
                continue
            annotations[param.name] = self._map_param_type(param)
        return annotations

    def _map_param_type(self, param: ToolParameterModel):
        ptype = (param.type or "string").lower()
        if ptype in ("integer", "int"):
            return int
        if ptype in ("number", "float", "double"):
            return float
        if ptype in ("bool", "boolean"):
            return bool
        if ptype in ("array", "list"):
            return list
        if ptype in ("object", "dict"):
            return dict
        return str

    def _coerce_default(self, value: str | None, param_type: str | None):
        if value is None:
            return None
        try:
            ptype = (param_type or "string").lower()
            if ptype in ("integer", "int"):
                return int(value)
            if ptype in ("number", "float", "double"):
                return float(value)
            if ptype in ("bool", "boolean"):
                return str(value).lower() in ("1", "true", "yes", "on")
            return value
        except Exception:
            return value


def compute_project_id(project_name: str, project_path: str) -> str:
    """
    DEPRECATED: Computes a SHA256-based project ID.
    This function is no longer used as of the multi-session fix.
    Unity instances now use their native project_hash (SHA1-based) for consistency
    across stdio and WebSocket transports.
    """
    combined = f"{project_name}:{project_path}"
    return sha256(combined.encode("utf-8")).hexdigest().upper()[:16]


def resolve_project_id_for_unity_instance(unity_instance: str | None) -> str | None:
    if unity_instance is None:
        return None

    # stdio transport: resolve via discovered instances with name+path
    try:
        pool = get_unity_connection_pool()
        instances = pool.discover_all_instances()
        target = None
        if "@" in unity_instance:
            name_part, _, hash_hint = unity_instance.partition("@")
            target = next(
                (
                    inst for inst in instances
                    if inst.name == name_part and inst.hash.startswith(hash_hint)
                ),
                None,
            )
        else:
            target = next(
                (
                    inst for inst in instances
                    if inst.id == unity_instance or inst.hash.startswith(unity_instance)
                ),
                None,
            )

        if target:
            # Return the project_hash from Unity (not a computed SHA256 hash).
            # This matches the hash Unity uses when registering tools via WebSocket.
            if target.hash:
                return target.hash
            logger.warning(
                f"Unity instance {target.id} has empty hash; cannot resolve project ID")
            return None
    except Exception:
        logger.debug(
            f"Failed to resolve project id via connection pool for {unity_instance}")

    # HTTP/WebSocket transport: resolve via PluginHub using project_hash
    try:
        hash_part: Optional[str] = None
        if "@" in unity_instance:
            _, _, suffix = unity_instance.partition("@")
            hash_part = suffix or None
        else:
            hash_part = unity_instance

        if hash_part:
            lowered = hash_part.lower()
            mapped: Optional[str] = None
            try:
                service = CustomToolService.get_instance()
                mapped = service.get_project_id_for_hash(lowered)
            except RuntimeError:
                mapped = None
            if mapped:
                return mapped
            return lowered
    except Exception:
        logger.debug(
            f"Failed to resolve project id via plugin hub for {unity_instance}")

    return None
