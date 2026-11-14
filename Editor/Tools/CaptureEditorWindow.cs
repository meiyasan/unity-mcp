#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Captures ANY open editor window to a PNG on disk — Inspector, Console, Game view, or a custom package
    /// EditorWindow — via the same GrabPixels path the Scene view uses, so it records the window's real
    /// on-screen pixels (IMGUI, UI Toolkit, gizmos, all). Focuses the window and forces an IMMEDIATE repaint
    /// first, so even a docked BACKGROUND tab is brought to front and drawn before the grab.
    /// Part of the package (it came from the Glitch Art Toolkit's MCP extensions): a generic action, for any project.
    /// </summary>
    [McpForUnityTool(
        name: "capture_editor_window",
        Description = "Capture any open Unity editor window (by type or title) to a PNG file, or list the open windows.")]
    public static class CaptureEditorWindow
    {
        /// <summary>Parameter schema. Without it the server serves a zero-argument signature and rejects every
        /// argument before Unity sees it.</summary>
        public class Parameters
        {
            [ToolParameter("'capture' writes a PNG of one window; 'list' enumerates the open windows.",
                           Required = false, DefaultValue = "capture")]
            public string action { get; set; }

            [ToolParameter("Window type name or title substring, e.g. 'SceneView', 'Console', 'Inspector'.",
                           Required = false)]
            public string window { get; set; }

            [ToolParameter("Absolute output path for the PNG.", Required = false)]
            public string path { get; set; }
        }

        private const int RepaintSettlingDelayMs = 75;

        public static object HandleCommand(JObject @params)
        {
            string action = @params["action"]?.ToString()?.ToLower();
            switch (action)
            {
                case "list":
                    return ListWindows();
                case "capture":
                    return Capture(@params);
                default:
                    return new ErrorResponse("Valid actions: list, capture. 'capture' takes 'window' (type name or title substring) and 'path' (absolute PNG path).");
            }
        }

        private static object ListWindows()
        {
            var windows = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null)
                .Select(w => new
                {
                    type = w.GetType().FullName,
                    title = w.titleContent?.text,
                    focused = EditorWindow.focusedWindow == w,
                    width = (int)w.position.width,
                    height = (int)w.position.height,
                })
                .ToList();
            return new SuccessResponse($"Found {windows.Count} open editor windows", new { windows });
        }

        private static object Capture(JObject @params)
        {
            string query = @params["window"]?.ToString();
            string path = @params["path"]?.ToString();
            if (string.IsNullOrEmpty(query))
                return new ErrorResponse("'window' parameter is required (window type name or title substring, e.g. 'SceneView', 'Console', 'Inspector').");
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' parameter is required (absolute file path for the PNG).");

            EditorWindow window = FindWindow(query);
            if (window == null)
                return new ErrorResponse($"No open editor window matches '{query}'. Use action 'list' to see what is open.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                CaptureWindowToFile(window, Path.GetFullPath(path));
                return new SuccessResponse($"Captured '{window.titleContent.text}' ({window.GetType().Name})", new
                {
                    path = Path.GetFullPath(path),
                    type = window.GetType().FullName,
                    title = window.titleContent.text,
                });
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Capture failed: {ex.Message}");
            }
        }

        public static EditorWindow FindWindow(string query)
        {
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null).ToList();
            return all.FirstOrDefault(w => string.Equals(w.GetType().Name, query, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => string.Equals(w.GetType().FullName, query, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => string.Equals(w.titleContent?.text, query, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => w.titleContent?.text?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static void CaptureWindowToFile(EditorWindow window, string absolutePath)
        {
            object hostView = GetHostView(window);
            if (hostView == null)
                throw new InvalidOperationException($"Failed to resolve host view for window '{window.titleContent.text}'.");

            try
            {
                window.Focus();
                window.Repaint();
                InvokeMethodIfExists(hostView, "RepaintImmediately");
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                EditorApplication.QueuePlayerLoopUpdate();
                Thread.Sleep(RepaintSettlingDelayMs);
            }
            catch (Exception ex)
            {
                Debug.Log($"[CaptureEditorWindow] Focus/repaint best-effort failed for '{window.titleContent.text}': {ex.Message}");
            }

            MethodInfo grabPixels = hostView.GetType().GetMethod(
                "GrabPixels",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(RenderTexture), typeof(Rect) }, null);
            if (grabPixels == null)
                throw new MissingMethodException($"{hostView.GetType().FullName}.GrabPixels(RenderTexture, Rect)");

            float ppp = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.RoundToInt(window.position.width * ppp);
            int height = Mathf.RoundToInt(window.position.height * ppp);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Window rect is empty.");

            RenderTexture rt = null;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
                rt.Create();
                grabPixels.Invoke(hostView, new object[] { rt, new Rect(0, 0, width, height) });
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                FlipTextureVertically(texture);
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static object GetHostView(EditorWindow window)
        {
            FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object parent = parentField?.GetValue(window);
            if (parent != null)
                return parent;
            PropertyInfo hostViewProperty = typeof(EditorWindow).GetProperty("hostView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return hostViewProperty?.GetValue(window, null);
        }

        private static void InvokeMethodIfExists(object instance, string methodName)
        {
            MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null || method.GetParameters().Length != 0)
                return;
            try { method.Invoke(instance, null); }
            catch (Exception ex) { Debug.Log($"[CaptureEditorWindow] Best-effort invoke of '{methodName}' failed: {ex.Message}"); }
        }

        private static void FlipTextureVertically(Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();
            var temp = new Color32[width];
            for (int y = 0; y < height / 2; y++)
            {
                int topRow = y * width;
                int bottomRow = (height - 1 - y) * width;
                Array.Copy(pixels, topRow, temp, 0, width);
                Array.Copy(pixels, bottomRow, pixels, topRow, width);
                Array.Copy(temp, 0, pixels, bottomRow, width);
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }
    }
}
