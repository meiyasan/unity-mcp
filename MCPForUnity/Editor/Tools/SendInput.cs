using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Mouse and keyboard input sent to one editor window as Unity events, not through the operating system:
    /// the editor may be behind other applications, the user's pointer does not move and no other window can
    /// receive it. IMGUI and UI Toolkit windows both take it. A game in Play reading the Input classes does
    /// not: those read the device, not the window's events.
    ///
    /// Positions are in points from the top-left corner of the window as capture_editor_window pictures it.
    /// On a docked window the first points are the tab strip, where a click changes tabs or starts dragging
    /// one: refused there unless asked for.
    /// </summary>
    [McpForUnityTool("send_input", AutoRegister = false)]
    public static class SendInput
    {
        private const float TabStripHeight = 24f;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return new ErrorResponse("Parameters cannot be null.");
            var p = new ToolParams(@params);
            string action = p.Get("action", "click").ToLowerInvariant();

            if (action == "list")
            {
                return new SuccessResponse("Open editor windows.", new { windows = AllWindows().Select(Describe).ToList() });
            }

            string windowName = p.Get("window");
            if (string.IsNullOrWhiteSpace(windowName))
                return new ErrorResponse("Required parameter 'window' is missing: a window's type name or part of its title (action 'list' shows them).");

            var matches = Find(windowName);
            if (matches.Count == 0)
                return new ErrorResponse($"No open editor window matches '{windowName}'.", new { windows = AllWindows().Select(Describe).ToList() });
            if (matches.Count > 1)
                return new ErrorResponse($"{matches.Count} open editor windows match '{windowName}': name one by its type.", new { windows = matches.Select(Describe).ToList() });
            EditorWindow window = matches[0];

            var modifiers = ParseModifiers(p.Get("modifiers"));
            int button = p.GetInt("button") ?? 0;
            var sent = new List<string>();

            try
            {
                switch (action)
                {
                    case "click":
                    case "double_click":
                    case "move":
                    case "scroll":
                    case "drag":
                    {
                        float? x = p.GetFloat("x"), y = p.GetFloat("y");
                        if (x == null || y == null) return new ErrorResponse($"'{action}' needs 'x' and 'y', in points from the window's top-left corner.");
                        var at = new Vector2(x.Value, y.Value);
                        string refusal = Refuse(window, at, p.GetBool("allow_tab_strip", false));
                        if (refusal != null) return new ErrorResponse(refusal, Describe(window));

                        if (action == "move")
                        {
                            Send(window, new Event { type = EventType.MouseMove, mousePosition = at, modifiers = modifiers }, sent);
                        }
                        else if (action == "scroll")
                        {
                            var delta = new Vector2(p.GetFloat("delta_x") ?? 0f, p.GetFloat("delta_y") ?? 0f);
                            if (delta == Vector2.zero) return new ErrorResponse("'scroll' needs 'delta_x' or 'delta_y' (positive delta_y scrolls down).");
                            Send(window, new Event { type = EventType.ScrollWheel, mousePosition = at, delta = delta, modifiers = modifiers }, sent);
                        }
                        else if (action == "drag")
                        {
                            float? toX = p.GetFloat("to_x"), toY = p.GetFloat("to_y");
                            if (toX == null || toY == null) return new ErrorResponse("'drag' needs 'to_x' and 'to_y'.");
                            var to = new Vector2(toX.Value, toY.Value);
                            refusal = Refuse(window, to, p.GetBool("allow_tab_strip", false));
                            if (refusal != null) return new ErrorResponse(refusal, Describe(window));
                            int steps = Mathf.Clamp(p.GetInt("steps") ?? 8, 1, 200);
                            Send(window, new Event { type = EventType.MouseDown, mousePosition = at, button = button, clickCount = 1, modifiers = modifiers }, sent);
                            Vector2 last = at;
                            for (int i = 1; i <= steps; i++)
                            {
                                Vector2 next = Vector2.Lerp(at, to, i / (float)steps);
                                Send(window, new Event { type = EventType.MouseDrag, mousePosition = next, delta = next - last, button = button, modifiers = modifiers }, sent);
                                last = next;
                            }
                            Send(window, new Event { type = EventType.MouseUp, mousePosition = to, button = button, clickCount = 1, modifiers = modifiers }, sent);
                        }
                        else
                        {
                            int clicks = action == "double_click" ? 2 : 1;
                            for (int c = 1; c <= clicks; c++)
                            {
                                Send(window, new Event { type = EventType.MouseDown, mousePosition = at, button = button, clickCount = c, modifiers = modifiers }, sent);
                                Send(window, new Event { type = EventType.MouseUp, mousePosition = at, button = button, clickCount = c, modifiers = modifiers }, sent);
                            }
                        }
                        break;
                    }

                    case "key":
                    {
                        string keyName = p.Get("key");
                        if (string.IsNullOrWhiteSpace(keyName)) return new ErrorResponse("'key' needs 'key': a KeyCode name (Return, Escape, Tab, A, F5, LeftArrow, ...).");
                        if (!Enum.TryParse(keyName, true, out KeyCode keyCode))
                            return new ErrorResponse($"'{keyName}' is not a KeyCode name.");
                        Send(window, new Event { type = EventType.KeyDown, keyCode = keyCode, modifiers = modifiers }, sent);
                        Send(window, new Event { type = EventType.KeyUp, keyCode = keyCode, modifiers = modifiers }, sent);
                        break;
                    }

                    case "text":
                    {
                        string text = p.Get("text");
                        if (string.IsNullOrEmpty(text)) return new ErrorResponse("'text' needs 'text': the characters to type into the window's focused field.");
                        foreach (char ch in text)
                            Send(window, new Event { type = EventType.KeyDown, character = ch, modifiers = modifiers }, sent);
                        break;
                    }

                    default:
                        return new ErrorResponse($"Unknown action '{action}'. Valid actions: click, double_click, move, drag, scroll, key, text, list.");
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Sending '{action}' to '{window.titleContent.text}' failed: {e.Message}", new { sent });
            }

            window.Repaint();
            return new SuccessResponse($"Sent {sent.Count} event(s) to '{window.titleContent.text}'.", new
            {
                window = Describe(window),
                events = sent.Count <= 12 ? sent : sent.Take(6).Concat(new[] { "..." }).Concat(sent.Skip(sent.Count - 5)).ToList(),
            });
        }

        private static void Send(EditorWindow window, Event e, List<string> sent)
        {
            window.SendEvent(e);
            sent.Add(e.isMouse || e.type == EventType.ScrollWheel
                ? $"{e.type} ({e.mousePosition.x:0.#}, {e.mousePosition.y:0.#})"
                : $"{e.type} {(e.character != 0 ? e.character.ToString() : e.keyCode.ToString())}");
        }

        private static string Refuse(EditorWindow window, Vector2 at, bool allowTabStrip)
        {
            Rect r = window.position;
            if (at.x < 0f || at.y < 0f || at.x > r.width || at.y > r.height)
                return $"({at.x:0.#}, {at.y:0.#}) is outside the window, which is {r.width:0} x {r.height:0} points.";
            if (window.docked && at.y < TabStripHeight && !allowTabStrip)
                return $"({at.x:0.#}, {at.y:0.#}) is on the docked window's tab strip (its first {TabStripHeight:0} points), where a click changes tabs or drags one. Pass allow_tab_strip=true to send it there.";
            return null;
        }

        private static List<EditorWindow> AllWindows() =>
            UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null).ToList();

        private static List<EditorWindow> Find(string name)
        {
            var all = AllWindows();
            var exact = all.Where(w => string.Equals(w.GetType().Name, name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(w.titleContent.text, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 0)
                exact = all.Where(w => (w.titleContent.text ?? string.Empty).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (exact.Count > 1)
            {
                // Several of one kind: the one in use, when exactly one is.
                var focused = exact.Where(w => w.hasFocus).ToList();
                if (focused.Count == 1) return focused;
            }
            return exact;
        }

        private static object Describe(EditorWindow w) => new
        {
            type = w.GetType().Name,
            title = w.titleContent.text,
            width = Mathf.RoundToInt(w.position.width),
            height = Mathf.RoundToInt(w.position.height),
            docked = w.docked,
            focused = w.hasFocus,
            pixels_per_point = EditorGUIUtility.pixelsPerPoint,
        };

        private static EventModifiers ParseModifiers(string text)
        {
            var result = EventModifiers.None;
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (string raw in text.Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "shift": result |= EventModifiers.Shift; break;
                    case "ctrl": case "control": result |= EventModifiers.Control; break;
                    case "alt": case "option": result |= EventModifiers.Alt; break;
                    case "cmd": case "command": case "meta": result |= EventModifiers.Command; break;
                }
            }
            return result;
        }
    }
}
