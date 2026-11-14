using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// What the last script compilation said: whether it failed, and each compiler error and warning with its file
    /// and line. Gathered from the pipeline as each assembly finishes, and kept across the script reload a successful
    /// compilation ends with (a failed one reloads nothing), so the answer is there for whoever asks after waiting
    /// for the editor: no second call to read the console and tell compiler errors from the rest.
    /// </summary>
    [InitializeOnLoad]
    public static class CompileResults
    {
        private const string Key = "MCPForUnity.CompileResults";
        private const int MaxMessages = 60;
        private static readonly object Gate = new object();
        private static JArray _errors = new JArray(), _warnings = new JArray();
        private static int _errorCount, _warningCount, _assemblies;
        private static long _startedUnixMs;
        private static string _last;   // the last finished compilation, as JSON

        static CompileResults()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            _last = SessionState.GetString(Key, null);
            CompilationPipeline.compilationStarted += _ =>
            {
                lock (Gate)
                {
                    _errors = new JArray(); _warnings = new JArray();
                    _errorCount = _warningCount = _assemblies = 0;
                    _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            };
            CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
            {
                lock (Gate)
                {
                    _assemblies++;
                    foreach (var m in messages)
                    {
                        bool error = m.type == CompilerMessageType.Error;
                        if (error) _errorCount++; else _warningCount++;
                        var list = error ? _errors : _warnings;
                        if (list.Count >= MaxMessages) continue;
                        list.Add(new JObject
                        {
                            ["file"] = m.file, ["line"] = m.line, ["column"] = m.column,
                            ["message"] = m.message,
                            ["assembly"] = System.IO.Path.GetFileNameWithoutExtension(assembly),
                        });
                    }
                }
            };
            CompilationPipeline.compilationFinished += _ =>
            {
                lock (Gate)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var result = new JObject
                    {
                        ["failed"] = _errorCount > 0,
                        ["error_count"] = _errorCount,
                        ["warning_count"] = _warningCount,
                        ["assemblies_compiled"] = _assemblies,
                        ["started_unix_ms"] = _startedUnixMs,
                        ["finished_unix_ms"] = now,
                        ["seconds"] = Math.Round((now - _startedUnixMs) / 1000.0, 2),
                        ["errors"] = _errors,
                        ["warnings"] = _warnings,
                    };
                    _last = result.ToString(Newtonsoft.Json.Formatting.None);
                    // Written before the reload that follows a success: the next domain reads it back.
                    SessionState.SetString(Key, _last);
                }
            };
        }

        /// <summary>
        /// The last compilation's result, or null when none has finished in this editor session. With
        /// <paramref name="warnings"/> false the warnings' text is left out (their count stays).
        /// </summary>
        public static JObject Last(bool warnings = false)
        {
            string last;
            lock (Gate) last = _last;
            if (string.IsNullOrEmpty(last)) return null;
            var result = JObject.Parse(last);
            if (!warnings) result.Remove("warnings");
            return result;
        }
    }
}
