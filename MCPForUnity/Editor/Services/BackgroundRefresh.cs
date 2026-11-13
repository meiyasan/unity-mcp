using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditorInternal;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Compiles without the focus. Unity notices changed files when its window is focused or when a refresh is asked
    /// for; an editor left in the background keeps the old code until then, and the compile starts as it is clicked.
    /// Here the project's compile inputs (scripts, assembly definitions, the package manifest) are watched from a
    /// background thread while the editor is not the active application, and a change that has settled is followed by
    /// an asset refresh on the main thread: the compile and reload are done by the time the editor is looked at.
    ///
    /// Not a file-system watcher: on a project of several hundred thousand files it would hold a handle per file.
    /// The script folders are walked instead (names only; a stat for each compile input), no more often than every
    /// <see cref="MinIntervalMs"/> and never for more than about a tenth of the time, whatever the project's size.
    ///
    /// Held back while the game plays, tests run, the editor compiles or imports, a progress bar is up, an open scene
    /// changed on disk (Unity would ask), or a tool holds it (<see cref="Hold"/>). Off when Unity's own Auto Refresh
    /// is off, or by the menu.
    /// </summary>
    [InitializeOnLoad]
    public static class BackgroundRefresh
    {
        private const string PrefKey = "MCPForUnity.BackgroundRefresh";
        private const string MenuPath = ProductInfo.MenuRoot + "/Refresh In Background";
        private const int MinIntervalMs = 2000;
        private static readonly string[] Extensions = { ".cs", ".asmdef", ".asmref", ".rsp" };

        private static Thread _thread;
        private static volatile bool _stop;
        private static volatile bool _watching;        // set on the main thread: the editor is in the background
        private static string[] _roots = Array.Empty<string>();
        private static string _manifest;

        // Written by the scanning thread, read on the main thread.
        private static readonly object Gate = new object();
        private static Signature _seen;                // the last scan
        private static bool _seenSettled;              // the same as the scan before it: nothing is being written
        private static Signature _handled;             // what the editor has been told about
        private static int _holds;

        private struct Signature : IEquatable<Signature>
        {
            public int Count;
            public long Newest;
            public long Mix;
            public bool Valid;
            public bool Equals(Signature o) => Valid == o.Valid && Count == o.Count && Newest == o.Newest && Mix == o.Mix;
        }

        /// <summary>Whether a changed script is compiled while the editor is in the background (kept per user).</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        /// <summary>
        /// Holds background refreshes back for as long as the returned object lives: a long job that must not be cut
        /// by a script reload wraps itself in <c>using (BackgroundRefresh.Hold())</c>.
        /// </summary>
        public static IDisposable Hold()
        {
            Interlocked.Increment(ref _holds);
            return new Release();
        }

        private sealed class Release : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _holds);
            }
        }

        static BackgroundRefresh()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || UnityEngine.Application.isBatchMode) return;
            try
            {
                string project = Path.GetDirectoryName(UnityEngine.Application.dataPath);
                var roots = new List<string> { UnityEngine.Application.dataPath, Path.Combine(project, "Packages") };
                // Packages kept elsewhere on this machine ("file:" in the manifest): theirs to compile as well.
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (package.source != UnityEditor.PackageManager.PackageSource.Local) continue;
                    if (!string.IsNullOrEmpty(package.resolvedPath) && Directory.Exists(package.resolvedPath)) roots.Add(package.resolvedPath);
                }
                _roots = roots.ToArray();
                _manifest = Path.Combine(project, "Packages", "manifest.json");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[BackgroundRefresh] not started: {ex.Message}");
                return;
            }

            EditorApplication.update += Tick;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += _ => _scenesKnownAt = DateTime.UtcNow;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (_, __) => _scenesKnownAt = DateTime.UtcNow;
            AssemblyReloadEvents.beforeAssemblyReload += () => _stop = true;
            EditorApplication.quitting += () => _stop = true;
            _thread = new Thread(Scan) { IsBackground = true, Name = "MCP background refresh", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        [MenuItem(MenuPath, priority = 60)]
        private static void Toggle() => Enabled = !Enabled;

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        private static void Tick()
        {
            bool background = !InternalEditorUtility.isApplicationActive;
            _watching = background && Enabled;

            Signature seen; bool settled;
            lock (Gate) { seen = _seen; settled = _seenSettled; }
            if (!seen.Valid) return;

            // In front, Unity refreshes by itself as it is focused: what was seen is its business, not ours.
            if (!background || !Enabled || !_handled.Valid)
            {
                _handled = seen;
                return;
            }

            if (!settled || seen.Equals(_handled)) return;
            if (!MayRefresh()) return;

            _handled = seen;
            McpLog.Info("[BackgroundRefresh] scripts changed while the editor was in the background: refreshing.", always: false);
            try
            {
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[BackgroundRefresh] refresh failed: {ex.Message}");
            }
        }

        private static bool MayRefresh()
        {
            if (Volatile.Read(ref _holds) > 0) return false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (EditorApplication.isUpdating || EditorStateCache.GetActualIsCompiling()) return false;
            if (TestRunStatus.IsRunning) return false;
            if (Progress.running) return false;
            // Unity's Auto Refresh switched off (Preferences ▸ Asset Pipeline): the user refreshes by hand.
            if (EditorPrefs.GetInt("kAutoRefreshMode", EditorPrefs.GetBool("kAutoRefresh", true) ? 1 : 0) == 0) return false;
            // An open scene whose file changed on disk makes the refresh ask, in a dialog nobody is there to answer.
            try
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (string.IsNullOrEmpty(scene.path) || !File.Exists(scene.path)) continue;
                    if (File.GetLastWriteTimeUtc(scene.path) > _scenesKnownAt) return false;
                }
            }
            catch { return false; }
            return true;
        }

        // When the editor last read or wrote its scenes (this domain's load, a scene opened or saved since): a scene
        // file newer than that was changed by someone else. Left to the focus, where the user answers Unity's question.
        private static DateTime _scenesKnownAt = DateTime.UtcNow;

        private static void Scan()
        {
            var clock = new Stopwatch();
            int wait = MinIntervalMs;
            while (!_stop)
            {
                for (int slept = 0; slept < wait && !_stop; slept += 100) Thread.Sleep(100);
                if (_stop) return;
                if (!_watching) continue;

                clock.Restart();
                Signature now;
                try { now = Read(); }
                catch { continue; } // a folder went away mid-walk: next time
                clock.Stop();
                // A tenth of the time at most: a walk of two seconds is taken again twenty seconds later.
                wait = (int)Math.Max(MinIntervalMs, clock.ElapsedMilliseconds * 10);

                lock (Gate)
                {
                    _seenSettled = _seen.Valid && _seen.Equals(now);
                    _seen = now;
                }
            }
        }

        private static Signature Read()
        {
            var signature = new Signature { Valid = true };
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in _roots) Walk(root, 0, visited, ref signature);
            if (_manifest != null && File.Exists(_manifest)) Add(_manifest, ref signature);
            return signature;
        }

        private static void Walk(string folder, int depth, HashSet<string> visited, ref Signature signature)
        {
            if (_stop || depth > 48) return;
            // Linked folders are followed (shared packages are links); each real folder once, whatever leads to it.
            string real;
            try { real = Path.GetFullPath(folder); } catch { return; }
            if (!visited.Add(real)) return;

            IEnumerable<string> files, folders;
            try
            {
                files = Directory.EnumerateFiles(folder);
                folders = Directory.EnumerateDirectories(folder);
            }
            catch { return; }

            foreach (string file in files)
            {
                foreach (string extension in Extensions)
                {
                    if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    Add(file, ref signature);
                    break;
                }
            }

            foreach (string child in folders)
            {
                string name = Path.GetFileName(child);
                // What Unity does not import: hidden folders, folders ending in ~, and build leftovers.
                if (name.Length == 0 || name[0] == '.' || name[name.Length - 1] == '~') continue;
                if (name == "node_modules" || name == "obj" || name == "Library" || name == "Temp") continue;
                Walk(child, depth + 1, visited, ref signature);
            }
        }

        private static void Add(string file, ref Signature signature)
        {
            long stamp;
            try { stamp = File.GetLastWriteTimeUtc(file).Ticks; } catch { return; }
            signature.Count++;
            if (stamp > signature.Newest) signature.Newest = stamp;
            unchecked { signature.Mix += stamp * 31 + StableHash(file); }
        }

        private static long StableHash(string text)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < text.Length; i++) hash = (hash ^ text[i]) * 1099511628211L;
                return hash;
            }
        }
    }
}
