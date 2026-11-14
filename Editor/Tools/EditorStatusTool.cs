#nullable disable
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using UnityEditor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Rich editor status over MCP — the bridge-up complement to the heartbeat file.
    ///
    /// If this tool ANSWERS, the editor main thread is alive; the payload then says what it is
    /// doing (compiling / updating / play mode / progress items). If it does NOT answer
    /// ("session disconnected", "not available"), that is NOT a crash: the editor is inside a
    /// synchronous batch — read <c>Library/GlitchArtStudio/EditorStatus.json</c> from the shell
    /// instead; it is kept current per imported asset even while the main thread is blocked,
    /// and its `unix` stamp tells you how long the current asset has been cooking.
    /// </summary>
    [McpForUnityTool(
        name: "editor_status",
        Description = "Editor liveness + activity: compiling/importing/play-mode flags, running Progress items, " +
                      "and the path of the heartbeat file that stays fresh even while the editor is blocked. " +
                      "If this call errors with a disconnect, the editor is BUSY (not dead) — read the heartbeat file.")]
    public static class EditorStatusTool
    {
        public class Parameters { }

        public static object HandleCommand(JObject @params)
        {
            var progress = new List<object>();
            foreach (var item in Progress.EnumerateItems())
            {
                if (item.status != Progress.Status.Running) continue;
                progress.Add(new { name = item.name, pct = (int)(item.progress * 100f) });
            }

            string state = EditorApplication.isCompiling ? "compiling"
                         : EditorApplication.isUpdating ? "importing"
                         : EditorApplication.isPlayingOrWillChangePlaymode ? "play-mode"
                         : "ready";

            return new SuccessResponse($"Editor is {state}" + (progress.Count > 0 ? $" ({progress.Count} task(s) running)" : ""), new
            {
                state,
                compiling = EditorApplication.isCompiling,
                updating = EditorApplication.isUpdating,
                playMode = EditorApplication.isPlayingOrWillChangePlaymode,
                progress,
                heartbeatFile = EditorStatusHeartbeat.StatusPath,
                hint = "Disconnect/timeout on ANY tool = editor busy on the main thread, not dead. " +
                       "Poll the heartbeat file from the shell; state+detail name the current asset."
            });
        }
    }
}
