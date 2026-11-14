using System;
using System.Collections.Generic;
using System.Threading;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// The last answers of read_console, kept so that the same question asked again of a console that has not
    /// changed is answered on the transport's receive thread, without waiting for an editor tick (200 ms behind
    /// another application). A poll is the usual case: the same read, repeated until something appears.
    ///
    /// An answer is reused only while nothing can have changed it: no message logged since (counted on whatever
    /// thread logs), the console's entry count as the main thread last saw it unchanged, no compilation started
    /// or finished, no clear through this tool, and the main thread alive to have noticed. Anything else, and
    /// the read goes to the main thread as before.
    /// </summary>
    internal static class ConsoleReadCache
    {
        private const int MaxEntries = 16;
        private const long MaxStallMs = 1000;

        private sealed class Entry
        {
            public string Json;
            public long Stamp;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Answers = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // Moves whenever the console may hold something else than before.
        private static long _stamp;
        private static int _lastCount = -1;
        private static bool _hooked;

        private static void Changed() => Interlocked.Increment(ref _stamp);

        /// <summary>Main thread: keeps the answer just given for these parameters.</summary>
        internal static void Store(JObject parameters, object result)
        {
            try
            {
                Hook();
                Sample();
                string json = JsonConvert.SerializeObject(new { status = "success", result });
                string key = Key(parameters);
                lock (Gate)
                {
                    if (Answers.Count >= MaxEntries && !Answers.ContainsKey(key)) Answers.Clear();
                    Answers[key] = new Entry { Json = json, Stamp = Interlocked.Read(ref _stamp) };
                }
            }
            catch
            {
                // The cache is a shortcut only.
            }
        }

        /// <summary>Main thread: the console was cleared, or may otherwise differ.</summary>
        internal static void Invalidate()
        {
            Changed();
            lock (Gate) Answers.Clear();
        }

        /// <summary>Any thread: the kept answer for these parameters, if the console cannot have changed.</summary>
        internal static bool TryGet(JObject parameters, out string json)
        {
            json = null;
            if (!_hooked || MainThreadHeartbeat.TickCount == 0 || MainThreadHeartbeat.StallMs > MaxStallMs) return false;
            string action = parameters?.Value<string>("action");
            if (action != null && !string.Equals(action, "get", StringComparison.OrdinalIgnoreCase)) return false;

            lock (Gate)
            {
                if (!Answers.TryGetValue(Key(parameters), out Entry entry)) return false;
                if (entry.Stamp != Interlocked.Read(ref _stamp)) return false;
                json = entry.Json;
                return true;
            }
        }

        private static string Key(JObject parameters) => parameters == null ? "{}" : parameters.ToString(Formatting.None);

        private static void Hook()
        {
            if (_hooked) return;
            Application.logMessageReceivedThreaded += (_, __, ___) => Changed();
            CompilationPipeline.compilationStarted += _ => Changed();
            CompilationPipeline.compilationFinished += _ => Changed();
            EditorApplication.update += Sample;
            _hooked = true;
        }

        // Compile errors and a clear from the Console window raise no log event: the entry count shows them.
        private static void Sample()
        {
            int count = ReadConsole.CurrentEntryCount();
            if (count == _lastCount) return;
            _lastCount = count;
            Changed();
        }
    }
}
