using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal; // Required for tag management
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles editor control actions including play mode control, tool selection,
    /// and tag/layer management. For reading editor state, use MCP resources instead.
    /// </summary>
    [McpForUnityTool("manage_editor", AutoRegister = false)]
    public static class ManageEditor
    {
        // Constant for starting user layer index
        private const int FirstUserLayerIndex = 8;

        // Constant for total layer count
        private const int TotalLayerCount = 32;

        /// <summary>
        /// Main handler for editor management actions.
        /// </summary>
        // Where the game is: playing or not, paused, the frame and the time it has reached, the scene.
        private static JObject PlayState()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return new JObject
            {
                ["is_playing"] = EditorApplication.isPlaying,
                ["is_paused"] = EditorApplication.isPaused,
                ["is_changing"] = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying,
                ["frame"] = EditorApplication.isPlaying ? Time.frameCount : 0,
                ["game_time"] = EditorApplication.isPlaying ? Math.Round(Time.time, 4) : 0.0,
                ["time_scale"] = Time.timeScale,
                ["runs_in_background"] = Application.runInBackground,
                ["editor_is_active"] = UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                ["scene"] = scene.path,
                ["scene_dirty"] = scene.isDirty,
            };
        }

        public static object HandleCommand(JObject @params)
        {
            // Step 1: Null parameter guard (consistent across all tools)
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            // Step 2: Wrap parameters
            var p = new ToolParams(@params);

            // Step 3: Extract and validate required parameters
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
            {
                return new ErrorResponse(actionResult.ErrorMessage);
            }
            string action = actionResult.Value.ToLowerInvariant();

            // Parameters for specific actions
            string tagName = p.Get("tagName");
            string layerName = p.Get("layerName");
            // Route action
            switch (action)
            {
                // Play Mode Control
                case "play":
                    try
                    {
                        return PlayModeStartupService.Start(@params);
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error entering play mode: {e.Message}");
                    }
                case "get_play_mode_startup":
                    return PlayModeStartupService.Get(@params["jobId"]?.ToString());
                case "pause":
                    try
                    {
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPaused = !EditorApplication.isPaused;
                            return new SuccessResponse(
                                EditorApplication.isPaused ? "Game paused." : "Game resumed."
                            );
                        }
                        return new ErrorResponse("Cannot pause/resume: Not in play mode.");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error pausing/resuming game: {e.Message}");
                    }
                case "play_status":
                    return new SuccessResponse("Play mode state.", PlayState());
                case "step":
                    // Frames, one after the other, with the game paused between calls. An editor that is not the
                    // active application gives a running game no frames (unless the project runs in the
                    // background): stepping is how a game is driven and observed from here whatever has the focus.
                    try
                    {
                        if (!EditorApplication.isPlaying) return new ErrorResponse("Cannot step: not in play mode.");
                        int frames = Mathf.Clamp(p.GetInt("frames") ?? 1, 1, 5000);
                        EditorApplication.isPaused = true;
                        int before = Time.frameCount; float timeBefore = Time.time;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        int done = 0;
                        // Bounded in real time too: a heavy game's frames must not hold the editor for minutes.
                        for (; done < frames && clock.ElapsedMilliseconds < 20000 && EditorApplication.isPlaying; done++) EditorApplication.Step();
                        var state = PlayState();
                        state["frames_stepped"] = Time.frameCount - before;
                        state["frames_asked"] = frames;
                        state["game_seconds_advanced"] = Math.Round(Time.time - timeBefore, 4);
                        state["real_ms"] = clock.ElapsedMilliseconds;
                        return new SuccessResponse(done < frames ? $"Stepped {done} of {frames} frames (stopped after 20 s)." : $"Stepped {done} frames; the game is paused.", state);
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error stepping the game: {e.Message}");
                    }
                case "run_in_background":
                    // Whether the running game gets frames while the editor is not the active application. For
                    // this run only: Unity sets it again from the Player settings at the next Play.
                    try
                    {
                        if (!EditorApplication.isPlaying) return new ErrorResponse("run_in_background applies to a running game: enter play mode first.");
                        Application.runInBackground = p.GetBool("enabled", true);
                        return new SuccessResponse(Application.runInBackground ? "The game runs while the editor is in the background." : "The game waits while the editor is in the background.", PlayState());
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error setting run_in_background: {e.Message}");
                    }
                case "compile_status":
                    {
                        var last = CompileResults.Last(p.GetBool("includeWarnings", false));
                        var data = new JObject
                        {
                            ["is_compiling"] = EditorApplication.isCompiling,
                            ["compilation_failed"] = EditorUtility.scriptCompilationFailed,
                            ["last_compilation"] = last,
                        };
                        return new SuccessResponse(last == null ? "No compilation has finished in this editor session." : (bool)last["failed"] ? $"The last compilation failed with {last["error_count"]} error(s)." : "The last compilation succeeded.", data);
                    }
                case "stop":
                    try
                    {
                        PlayModeStartupService.CancelWaiting();
                        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                        {
                            // Not left paused: a paused editor enters the next Play paused, and looks frozen.
                            EditorApplication.isPaused = false;
                            EditorApplication.isPlaying = false;
                            return new SuccessResponse("Exited play mode.");
                        }
                        return new SuccessResponse("Already stopped (not in play mode).");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error stopping play mode: {e.Message}");
                    }

                // Tool Control
                case "set_active_tool":
                    var toolNameResult = p.GetRequired("toolName", "'toolName' parameter required for set_active_tool.");
                    if (!toolNameResult.IsSuccess)
                        return new ErrorResponse(toolNameResult.ErrorMessage);
                    return SetActiveTool(toolNameResult.Value);

                // Tag Management
                case "add_tag":
                    var addTagResult = p.GetRequired("tagName", "'tagName' parameter required for add_tag.");
                    if (!addTagResult.IsSuccess)
                        return new ErrorResponse(addTagResult.ErrorMessage);
                    return AddTag(addTagResult.Value);
                case "remove_tag":
                    var removeTagResult = p.GetRequired("tagName", "'tagName' parameter required for remove_tag.");
                    if (!removeTagResult.IsSuccess)
                        return new ErrorResponse(removeTagResult.ErrorMessage);
                    return RemoveTag(removeTagResult.Value);
                // Layer Management
                case "add_layer":
                    var addLayerResult = p.GetRequired("layerName", "'layerName' parameter required for add_layer.");
                    if (!addLayerResult.IsSuccess)
                        return new ErrorResponse(addLayerResult.ErrorMessage);
                    return AddLayer(addLayerResult.Value);
                case "remove_layer":
                    var removeLayerResult = p.GetRequired("layerName", "'layerName' parameter required for remove_layer.");
                    if (!removeLayerResult.IsSuccess)
                        return new ErrorResponse(removeLayerResult.ErrorMessage);
                    return RemoveLayer(removeLayerResult.Value);
                // --- Settings (Example) ---
                // case "set_resolution":
                //     int? width = @params["width"]?.ToObject<int?>();
                //     int? height = @params["height"]?.ToObject<int?>();
                //     if (!width.HasValue || !height.HasValue) return new ErrorResponse("'width' and 'height' parameters required.");
                //     return SetGameViewResolution(width.Value, height.Value);
                // case "set_quality":
                //     // Handle string name or int index
                //     return SetQualityLevel(@params["qualityLevel"]);

                // Package Deployment
                case "deploy_package":
                    return DeployPackage();
                case "restore_package":
                    return RestorePackage();

                // Undo/Redo
                case "undo":
                {
                    string groupName = Undo.GetCurrentGroupName();
                    Undo.PerformUndo();
                    string message = string.IsNullOrEmpty(groupName)
                        ? "Undo performed (stack may be empty)."
                        : $"Undid: {groupName}";
                    if (EditorApplication.isPlaying)
                        message += " Warning: undo during play mode may have unexpected effects.";
                    return new SuccessResponse(message, new
                    {
                        undone_group = string.IsNullOrEmpty(groupName) ? (string)null : groupName,
                        next_group = Undo.GetCurrentGroupName()
                    });
                }
                case "redo":
                {
                    Undo.PerformRedo();
                    string nextGroup = Undo.GetCurrentGroupName();
                    string message = "Redo performed.";
                    if (EditorApplication.isPlaying)
                        message += " Warning: redo during play mode may have unexpected effects.";
                    return new SuccessResponse(message, new
                    {
                        current_group = string.IsNullOrEmpty(nextGroup) ? (string)null : nextGroup
                    });
                }

                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Supported actions: play, get_play_mode_startup, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, deploy_package, restore_package, undo, redo. For prefab editing (open/save/close prefab stage), use manage_prefabs. Use MCP resources for reading editor state, project info, tags, layers, selection, windows, prefab stage, and active tool."
                    );
            }
        }

        // --- Tool Control Methods ---

        private static object SetActiveTool(string toolName)
        {
            try
            {
                Tool targetTool;
                if (Enum.TryParse<Tool>(toolName, true, out targetTool)) // Case-insensitive parse
                {
                    // Check if it's a valid built-in tool
                    if (targetTool != Tool.None && targetTool <= Tool.Custom) // Tool.Custom is the last standard tool
                    {
                        UnityEditor.Tools.current = targetTool;
                        return new SuccessResponse($"Set active tool to '{targetTool}'.");
                    }
                    else
                    {
                        return new ErrorResponse(
                            $"Cannot directly set tool to '{toolName}'. It might be None, Custom, or invalid."
                        );
                    }
                }
                else
                {
                    // Potentially try activating a custom tool by name here if needed
                    // This often requires specific editor scripting knowledge for that tool.
                    return new ErrorResponse(
                        $"Could not parse '{toolName}' as a standard Unity Tool (View, Move, Rotate, Scale, Rect, Transform, Custom)."
                    );
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error setting active tool: {e.Message}");
            }
        }

        // --- Tag Management Methods ---

        private static object AddTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return new ErrorResponse("Tag name cannot be empty or whitespace.");

            // Check if tag already exists
            if (System.Linq.Enumerable.Contains(InternalEditorUtility.tags, tagName))
            {
                return new ErrorResponse($"Tag '{tagName}' already exists.");
            }

            try
            {
                // Add the tag using the internal utility
                InternalEditorUtility.AddTag(tagName);
                // Force save assets to ensure the change persists in the TagManager asset
                AssetDatabase.SaveAssets();
                return new SuccessResponse($"Tag '{tagName}' added successfully.");
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to add tag '{tagName}': {e.Message}");
            }
        }

        private static object RemoveTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return new ErrorResponse("Tag name cannot be empty or whitespace.");
            if (tagName.Equals("Untagged", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("Cannot remove the built-in 'Untagged' tag.");

            // Check if tag exists before attempting removal
            if (!System.Linq.Enumerable.Contains(InternalEditorUtility.tags, tagName))
            {
                return new ErrorResponse($"Tag '{tagName}' does not exist.");
            }

            try
            {
                // Remove the tag using the internal utility
                InternalEditorUtility.RemoveTag(tagName);
                // Force save assets
                AssetDatabase.SaveAssets();
                return new SuccessResponse($"Tag '{tagName}' removed successfully.");
            }
            catch (Exception e)
            {
                // Catch potential issues if the tag is somehow in use or removal fails
                return new ErrorResponse($"Failed to remove tag '{tagName}': {e.Message}");
            }
        }

        // --- Layer Management Methods ---

        private static object AddLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return new ErrorResponse("Layer name cannot be empty or whitespace.");

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return new ErrorResponse("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return new ErrorResponse("Could not find 'layers' property in TagManager.");

            // Check if layer name already exists (case-insensitive check recommended)
            for (int i = 0; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return new ErrorResponse($"Layer '{layerName}' already exists at index {i}.");
                }
            }

            // Find the first empty user layer slot (indices 8 to 31)
            int firstEmptyUserLayer = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (layerSP != null && string.IsNullOrEmpty(layerSP.stringValue))
                {
                    firstEmptyUserLayer = i;
                    break;
                }
            }

            if (firstEmptyUserLayer == -1)
            {
                return new ErrorResponse("No empty User Layer slots available (8-31 are full).");
            }

            // Assign the name to the found slot
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    firstEmptyUserLayer
                );
                targetLayerSP.stringValue = layerName;
                // Apply the changes to the TagManager asset
                tagManager.ApplyModifiedProperties();
                // Save assets to make sure it's written to disk
                AssetDatabase.SaveAssets();
                return new SuccessResponse(
                    $"Layer '{layerName}' added successfully to slot {firstEmptyUserLayer}."
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to add layer '{layerName}': {e.Message}");
            }
        }

        private static object RemoveLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return new ErrorResponse("Layer name cannot be empty or whitespace.");

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return new ErrorResponse("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return new ErrorResponse("Could not find 'layers' property in TagManager.");

            // Find the layer by name (must be user layer)
            int layerIndexToRemove = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++) // Start from user layers
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                // Case-insensitive comparison is safer
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    layerIndexToRemove = i;
                    break;
                }
            }

            if (layerIndexToRemove == -1)
            {
                return new ErrorResponse($"User layer '{layerName}' not found.");
            }

            // Clear the name for that index
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    layerIndexToRemove
                );
                targetLayerSP.stringValue = string.Empty; // Set to empty string to remove
                // Apply the changes
                tagManager.ApplyModifiedProperties();
                // Save assets
                AssetDatabase.SaveAssets();
                return new SuccessResponse(
                    $"Layer '{layerName}' (slot {layerIndexToRemove}) removed successfully."
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to remove layer '{layerName}': {e.Message}");
            }
        }

        // --- Package Deployment Methods ---

        private static object DeployPackage()
        {
            try
            {
                var result = MCPServiceLocator.Deployment.DeployFromStoredSource();
                if (!result.Success)
                    return new ErrorResponse(result.Message);

                return new SuccessResponse(result.Message, new
                {
                    source_path = result.SourcePath,
                    target_path = result.TargetPath,
                    backup_path = result.BackupPath
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Deploy failed: {e.Message}");
            }
        }

        private static object RestorePackage()
        {
            try
            {
                var result = MCPServiceLocator.Deployment.RestoreLastBackup();
                if (!result.Success)
                    return new ErrorResponse(result.Message);

                return new SuccessResponse(result.Message, new
                {
                    target_path = result.TargetPath,
                    backup_path = result.BackupPath
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Restore failed: {e.Message}");
            }
        }

        // --- Helper Methods ---

        /// <summary>
        /// Gets the SerializedObject for the TagManager asset.
        /// </summary>
        private static SerializedObject GetTagManager()
        {
            try
            {
                // Load the TagManager asset from the ProjectSettings folder
                UnityEngine.Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(
                    "ProjectSettings/TagManager.asset"
                );
                if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                {
                    McpLog.Error("[ManageEditor] TagManager.asset not found in ProjectSettings.");
                    return null;
                }
                // The first object in the asset file should be the TagManager
                return new SerializedObject(tagManagerAssets[0]);
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageEditor] Error accessing TagManager.asset: {e.Message}");
                return null;
            }
        }

        // --- Example Implementations for Settings ---
        /*
        private static object SetGameViewResolution(int width, int height) { ... }
        private static object SetQualityLevel(JToken qualityLevelToken) { ... }
        */
    }
}
