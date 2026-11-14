#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Editor-status heartbeat for agents (and humans) working over MCP.
    ///
    /// THE PROBLEM THIS SOLVES: while the editor's main thread is inside a synchronous batch
    /// (importer-version reimport, domain reload, long warp solve), the MCP bridge cannot answer —
    /// every tool call returns a bare "success:false / session disconnected", which is
    /// indistinguishable from a crash and repeatedly misleads agents into treating a BUSY editor
    /// as a DEAD one (or worse, treating stale artifacts as fresh results). An in-editor TOOL can
    /// never fix that: it needs the main thread too. A FILE can.
    ///
    /// HOW IT SURVIVES A BLOCKED MAIN THREAD: a BACKGROUND THREAD writes the file. The main thread only
    /// records what it knows (state, detail, a Progress snapshot) into fields and stamps a liveness tick; the
    /// writer thread publishes that every 2s whatever the main thread is doing. This is the whole point, and
    /// for a long time it was not true — the writer was on EditorApplication.update, so a wedged editor froze
    /// the file at its LAST value. Tonight that value was "ready", and a hung editor was indistinguishable
    /// from a healthy idle one for 25 minutes. `mainThreadAgeMs` is the field that tells them apart.
    ///
    /// This class keeps <c>Library/GlitchArtStudio/EditorStatus.json</c> current through every state
    /// the editor passes through:
    ///   ready        — update loop ticking, nothing pending (heartbeat refreshed every 2s)
    ///   compiling    — script compilation in progress
    ///   domain-reload— between beforeAssemblyReload and the next InitializeOnLoad
    ///   importing    — inside an asset batch; `detail` names the CURRENT asset (written from
    ///                  OnPreprocessAsset, which ticks per asset even while the main loop is blocked)
    ///   play-mode    — entering / leaving / in play mode
    ///   quitting     — editor shutting down
    ///
    /// READING IT (bash, no bridge needed):
    ///   cat "Library/GlitchArtStudio/EditorStatus.json"
    /// Fields: state, detail, progress (Progress-API snapshot), utc, unix, sessionPid.
    /// READ `mainThreadAgeMs` FIRST — it is how long the editor's update loop has been silent:
    ///   small (< ~4000)            — main thread is ticking; `state` means what it says
    ///   large while "importing X"  — normal for one slow asset (a large importer can take minutes)
    ///   large while "tool-running" — normal for a long tool (a batch comparison, say)
    ///   large while "ready"        — check `cpuPct`: busy = working with the loop blocked (an
    ///                                 execute_code batch never stamps a state, so it looks like this);
    ///                                 near zero = THE EDITOR IS WEDGED, reported as state "blocked".
    /// The file itself keeps updating in every case, because the writer is not on the main thread.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorStatusHeartbeat
    {
        static double s_NextBeat;
        static string s_LastState = "ready", s_LastDetail;
        // Written by the main thread, read by the writer thread. `object` lock rather than volatile
        // strings so state+detail+progress are always published as one consistent set.
        static readonly object s_Gate = new object();
        static string s_Progress = "[]";
        // Read on the main thread: SystemInfo.battery* throws anywhere else ("can only be called from the main
        // thread"), and inside Publish that exception, swallowed, cancelled every write of the background timer -
        // the file only ever moved when the main thread published, i.e. never while it was busy.
        static string s_Power = "unknown";
        // Stopwatch rather than Environment.TickCount64: that is a .NET Core 3.0+ API and is not
        // guaranteed under Unity's .NET Standard profile, while Stopwatch is available everywhere.
        static readonly System.Diagnostics.Stopwatch s_Clock = System.Diagnostics.Stopwatch.StartNew();
        static long s_LastTickMs;                    // s_Clock.ElapsedMilliseconds at the last main-thread tick
        static System.Threading.Timer s_Writer;
        static int s_Pid;
        // CPU is what separates "wedged" from "working with the update loop blocked". A silent update loop
        // says nothing on its own: a batch comparison, an external exporter and a heavy solve all block it
        // for minutes while the process burns CPU, whereas a genuine wedge sits at 0%. Sampled on the
        // WRITER thread from getrusage(RUSAGE_SELF).
        //
        // NOT Process.TotalProcessorTime: on macOS Unity's Mono answers it with task_threads() and never frees
        // the thread list it gets back, so every call leaks a "Mach message" region the size of that list.
        // Called every 2 s, it had leaked 35,000 regions (3 GB) into an editor up for 29 hours; measured in
        // isolation under the same Mono, 2,000 calls left 573 regions and getrusage left none.
        static System.Diagnostics.Process s_Proc;
        static TimeSpan s_LastCpu;
        static long s_LastCpuAtMs;
        static double s_CpuPct;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct TimeVal { public long Sec; public int Usec; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RUsage
        {
            public TimeVal UTime, STime;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 14)]
            public long[] Rest;
        }
        [System.Runtime.InteropServices.DllImport("libc")] static extern int getrusage(int who, ref RUsage usage);
        static bool s_NoRusage;

        // MEMORY, SO A CLIMB CAN BE PINNED ON WHAT CAUSED IT. An editor up for 29 hours reached a 33 GB footprint
        // and nothing recorded when it grew or during what. The main thread samples the Mono heap and Unity's own
        // allocators (Profiler APIs are main-thread only); the writer thread reads the process footprint, the
        // number Activity Monitor shows, from proc_pid_rusage. Once a minute, and on every state change, a line
        // goes to Library/GlitchArtStudio/MemoryLog.csv with what the editor was doing at that moment.
        static long s_MonoHeap, s_MonoUsed, s_NativeAlloc, s_NativeReserved;
        [System.Runtime.InteropServices.DllImport("libproc")]
        static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);
        static bool s_NoFootprint;
        static long s_NextMemLogMs;
        static string s_LastLoggedState;
        static readonly object s_MemLogGate = new object();
        const long MemLogIntervalMs = 60000, MemLogMaxBytes = 4L << 20;

        /// <summary>phys_footprint from rusage_info_v2 (16-byte uuid, then seven u64 fields before it), or -1.</summary>
        static long FootprintBytes()
        {
            if (s_NoFootprint) return -1;
            try
            {
                var buf = new byte[256];
                if (proc_pid_rusage(s_Pid, 2 /* RUSAGE_INFO_V2 */, buf) != 0) return -1;
                return BitConverter.ToInt64(buf, 16 + 8 * 7);
            }
            catch (DllNotFoundException) { s_NoFootprint = true; }
            catch (EntryPointNotFoundException) { s_NoFootprint = true; }
            return -1;
        }

        internal static string MemoryLogPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "GlitchArtStudio", "MemoryLog.csv");

        static void AppendMemoryLog(string state, string detail, long footprint)
        {
            long now = s_Clock.ElapsedMilliseconds;
            string key = state + "|" + detail;
            if (now < s_NextMemLogMs && key == s_LastLoggedState) return;
            s_NextMemLogMs = now + MemLogIntervalMs;
            s_LastLoggedState = key;
            string path = MemoryLogPath;
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MemLogMaxBytes)
            {
                // Keep the newer half: the recent climb is what anyone opens this file for.
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(lines.Length / 2));
            }
            bool header = !File.Exists(path);
            string MB(long b) => b < 0 ? "" : (b / 1048576).ToString(CultureInfo.InvariantCulture);
            string csv(string v) => v == null ? "" : "\"" + v.Replace("\"", "'") + "\"";
            File.AppendAllText(path,
                (header ? "utc,pid,footprintMB,monoHeapMB,monoUsedMB,nativeAllocMB,nativeReservedMB,state,detail\n" : "") +
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," + s_Pid + "," +
                MB(footprint) + "," + MB(System.Threading.Interlocked.Read(ref s_MonoHeap)) + "," +
                MB(System.Threading.Interlocked.Read(ref s_MonoUsed)) + "," + MB(System.Threading.Interlocked.Read(ref s_NativeAlloc)) + "," +
                MB(System.Threading.Interlocked.Read(ref s_NativeReserved)) + "," + state + "," + csv(detail) + "\n");
        }

        /// <summary>This process's user + system CPU time. getrusage where libc has it; elsewhere (Windows)
        /// TotalProcessorTime, whose leak is specific to Mono on macOS.</summary>
        static TimeSpan ProcessCpu()
        {
            if (!s_NoRusage)
            {
                try
                {
                    var u = new RUsage();
                    if (getrusage(0, ref u) == 0)
                        return TimeSpan.FromTicks((u.UTime.Sec + u.STime.Sec) * TimeSpan.TicksPerSecond
                                                  + (long)(u.UTime.Usec + u.STime.Usec) * 10);
                }
                catch (DllNotFoundException) { s_NoRusage = true; }
                catch (EntryPointNotFoundException) { s_NoRusage = true; }
            }
            return s_Proc.TotalProcessorTime;
        }

        public static string StatusPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "GlitchArtStudio", "EditorStatus.json");

        static EditorStatusHeartbeat()
        {
            // THE EDITOR ONLY. Asset import workers are separate Unity processes that load the same editor
            // assemblies, so each ran its own heartbeat and wrote "ready" with its own pid into the editor's
            // status file (seen in the first MemoryLog.csv lines: pids 79600/79602 at 780 MB beside the editor).
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            s_Proc = System.Diagnostics.Process.GetCurrentProcess();
            s_Pid = s_Proc.Id;
            s_LastCpu = ProcessCpu();
            s_LastCpuAtMs = s_Clock.ElapsedMilliseconds;
            s_LastTickMs = s_Clock.ElapsedMilliseconds;
            EditorApplication.update += OnUpdate;
            // The background publisher. Unity APIs are main-thread only, so this thread never touches them —
            // it formats fields the main thread already filled in and writes the file.
            s_Writer = new System.Threading.Timer(_ => Publish(), null, 0, 2000);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                // Stop the writer BEFORE the domain goes away, or the timer callback runs against a
                // half-unloaded appdomain and throws on every tick.
                try { s_Writer?.Dispose(); } catch { } finally { s_Writer = null; }
                Write("domain-reload", "assembly reload starting");
            };
            EditorApplication.quitting += () => Write("quitting", null);
            EditorApplication.playModeStateChanged += c => Write("play-mode", c.ToString());
            // First stamp after every domain reload — this is also what flips domain-reload → ready.
            Write(EditorApplication.isCompiling ? "compiling" : "ready", "domain reload finished");
        }

        static void OnUpdate()
        {
            // Liveness EVERY tick, not every 2s — this is the number that distinguishes a wedged editor
            // from an idle one, so it must be as fresh as the update loop itself.
            System.Threading.Interlocked.Exchange(ref s_LastTickMs, s_Clock.ElapsedMilliseconds);
            if (EditorApplication.timeSinceStartup < s_NextBeat) return;
            s_NextBeat = EditorApplication.timeSinceStartup + 2.0;

            string state = "ready";
            string detail = null;
            if (EditorApplication.isCompiling) { state = "compiling"; }
            else if (EditorApplication.isUpdating) { state = "importing"; detail = "asset database updating"; }
            else if (EditorApplication.isPlayingOrWillChangePlaymode) { state = "play-mode"; detail = "playing"; }
            Write(state, detail);
        }

        /// <summary>Per-asset tick inside blocking import batches (see ImportBeat below).</summary>
        internal static void ImportTick(string assetPath)
        {
            // Unthrottled on purpose: during a batch the 2s update throttle never runs, and each
            // asset may take seconds to minutes.
            Write("importing", assetPath);
        }

        /// <summary>Stamped by ImportBeat when a batch completes.</summary>
        internal static void BatchDone(int count) =>
            Write("ready", "import batch finished: " + count + " asset(s)");

        /// <summary>
        /// For long-running MCP tools: stamp entry with the tool
        /// name + args, stamp completion from a finally. The bridge usually times out long before
        /// the tool finishes, so this is the only place the CALLER can learn what is running.
        /// </summary>
        public static void ToolRunning(string toolAndArgs) => Write("tool-running", toolAndArgs);
        public static void ToolDone(string toolAndArgs) => Write("ready", "finished: " + toolAndArgs);

        /// <summary>Record what the MAIN THREAD knows. Does not touch the file — the writer thread
        /// publishes it. Safe to call as often as callers like; it is a few field assignments.</summary>
        static void Write(string state, string detail)
        {
            if (s_Proc == null) return;   // an asset import worker (see the static constructor)
            try
            {
                // Progress is a Unity API, so it can only be read here. Snapshot it as text.
                string progress = "[]";
                try
                {
                    var sb = new StringBuilder(128);
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in Progress.EnumerateItems())
                    {
                        if (item.status != Progress.Status.Running) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"name\":").Append(Json(item.name))
                          .Append(",\"pct\":").Append((item.progress * 100f).ToString("F0", CultureInfo.InvariantCulture))
                          .Append('}');
                    }
                    sb.Append(']');
                    progress = sb.ToString();
                }
                catch { }

                try
                {
                    System.Threading.Interlocked.Exchange(ref s_MonoHeap, UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong());
                    System.Threading.Interlocked.Exchange(ref s_MonoUsed, UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong());
                    System.Threading.Interlocked.Exchange(ref s_NativeAlloc, UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
                    System.Threading.Interlocked.Exchange(ref s_NativeReserved, UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong());
                }
                catch { }

                string power = s_Power;
                try { power = PowerTag(); } catch { }

                lock (s_Gate) { s_LastState = state; s_LastDetail = detail; s_Progress = progress; s_Power = power; }
                System.Threading.Interlocked.Exchange(ref s_LastTickMs, s_Clock.ElapsedMilliseconds);
                Publish();   // publish immediately so a state CHANGE is never up to 2s late
            }
            catch { /* the heartbeat must never break the editor */ }
        }

        /// <summary>Write the file. Runs on the BACKGROUND TIMER (and inline on state changes), so it must
        /// not call a single Unity API — everything it needs was captured by the main thread.</summary>
        static void Publish()
        {
            try
            {
                string state, detail, progress, power;
                lock (s_Gate) { state = s_LastState; detail = s_LastDetail; progress = s_Progress; power = s_Power; }

                long ageMs = s_Clock.ElapsedMilliseconds - System.Threading.Interlocked.Read(ref s_LastTickMs);
                if (ageMs < 0) ageMs = 0;

                // Process CPU since the last publish, as a percentage of one core.
                try
                {
                    long nowMs = s_Clock.ElapsedMilliseconds;
                    var cpu = ProcessCpu();
                    double wall = nowMs - s_LastCpuAtMs;
                    if (wall >= 250)
                    {
                        s_CpuPct = (cpu - s_LastCpu).TotalMilliseconds / wall * 100.0;
                        s_LastCpu = cpu; s_LastCpuAtMs = nowMs;
                    }
                }
                catch { }

                // A silent update loop is NOT a wedge by itself — a batch comparison, an external export or a heavy solve
                // all block it for minutes. It is a wedge when the loop is silent AND the process is idle.
                // Requiring both is what stops this crying wolf on every long job: measured on a running
                // batch comparison the editor sat at 15-20% CPU with the loop silent for 77s, which the
                // age-only rule reported as "blocked" when it was working perfectly well.
                string reported = state;
                if (ageMs > 30000 && state == "ready" && s_CpuPct < 2.0) reported = "blocked";

                long footprint = FootprintBytes();
                // Publish runs on the timer thread AND inline on the main thread, so the log is serialised.
                try { lock (s_MemLogGate) AppendMemoryLog(reported, detail, footprint); } catch { }

                var sb = new StringBuilder(512);
                var now = DateTime.UtcNow;
                sb.Append("{\"state\":\"").Append(reported)
                  .Append("\",\"detail\":").Append(detail == null ? "null" : Json(detail))
                  .Append(",\"mainThreadAgeMs\":").Append(ageMs)
                  .Append(",\"cpuPct\":").Append(s_CpuPct.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"underlyingState\":\"").Append(state)
                  .Append("\",\"utc\":\"").Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .Append("\",\"unix\":").Append(((DateTimeOffset)now).ToUnixTimeSeconds())
                  .Append(",\"sessionPid\":").Append(s_Pid)
                  .Append(",\"footprintMB\":").Append(footprint < 0 ? "null" : (footprint / 1048576).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"monoHeapMB\":").Append((System.Threading.Interlocked.Read(ref s_MonoHeap) / 1048576).ToString(CultureInfo.InvariantCulture))
                  // WHAT THE MACHINE WAS DOING THIS ON. A duration reported without them cannot be read: a Mac on
                  // battery throttles hard, and the importer/compare work is largely single-threaded, so a
                  // 16-core machine lends about one and a half cores to it — which is why cpuPct sitting near
                  // 150% during a long run is normal, not a stall.
                  // WHAT A LONG RUN IS DOING, as the project's own batch tools report it (see StatusLines): a
                  // batch that works one item per tick keeps this line current; without it the heartbeat goes
                  // silent for minutes and a busy editor is indistinguishable from a hung one.
                  .Append(",\"batch\":").Append(Json(BatchLine()))
                  .Append(",\"machine\":").Append(Json(s_Machine))
                  .Append(",\"power\":").Append(Json(power))
                  .Append(",\"progress\":").Append(progress)
                  .Append('}');

                string path = StatusPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString());
            }
            catch { /* never break the editor, and never let the timer thread throw */ }
        }

        // Static: the chip and core count cannot change while the editor runs.
        static readonly string s_Machine =
            $"{UnityEngine.SystemInfo.processorType} \u00b7 {UnityEngine.SystemInfo.processorCount} cores \u00b7 " +
            $"{UnityEngine.Mathf.RoundToInt(UnityEngine.SystemInfo.systemMemorySize / 1024f)} GB";

        // NOT static: the cable comes and goes, and that is exactly what a timing must be read against.
        // MAIN THREAD ONLY (see s_Power).
        static string PowerTag()
        {
            var lvl = UnityEngine.SystemInfo.batteryLevel;
            string pct = lvl >= 0f ? $" {UnityEngine.Mathf.RoundToInt(lvl * 100f)}%" : "";
            switch (UnityEngine.SystemInfo.batteryStatus)
            {
                case UnityEngine.BatteryStatus.Charging:
                case UnityEngine.BatteryStatus.Full:        return "AC" + pct;
                case UnityEngine.BatteryStatus.Discharging: return "battery" + pct;
                default:                                    return "unknown";
            }
        }

        // A batch tool says what it is doing with a public static string StatusLine on any of its editor types
        // ("" when idle), read by name so that nothing depends on this package. Found once, in the project's own
        // assemblies (Unity's and the runtime's are skipped); the writer thread only reads the properties.
        static readonly System.Reflection.PropertyInfo[] s_StatusLines = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !System.Text.RegularExpressions.Regex.IsMatch(a.GetName().Name, @"^(Unity|UnityEngine|UnityEditor|System|mscorlib|Mono|netstandard|Microsoft|nunit|Bee)\b"))
            .SelectMany(a => { try { return a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } })
            .Select(t => t.GetProperty("StatusLine", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(p => p != null && p.PropertyType == typeof(string))
            .OrderBy(p => p.DeclaringType.FullName, StringComparer.Ordinal).ToArray();

        // The lines of every tool busy now, joined.
        static string BatchLine() => string.Join(" \u00b7 ", s_StatusLines
            .Select(p => { try { return p.GetValue(null) as string; } catch { return null; } })
            .Where(l => !string.IsNullOrEmpty(l)));

        static string Json(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Ticks the heartbeat once per asset while a (blocking) import batch runs.</summary>
    sealed class ImportBeat : AssetPostprocessor
    {
        void OnPreprocessAsset()
        {
            // Every asset: a stamp only records fields (the writer thread publishes them every 2s), so it costs
            // nothing, and the slow asset is the one named when the batch stalls.
            EditorStatusHeartbeat.ImportTick(assetImporter.assetPath);
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // Batch finished — next update tick would stamp "ready" anyway, but stamp it eagerly so
            // the file goes ready even if the editor immediately blocks on something without updates.
            EditorStatusHeartbeat.BatchDone(imported.Length);
        }
    }
}
