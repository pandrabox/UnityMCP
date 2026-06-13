using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    [InitializeOnLoad]
    internal sealed class CompileCommandHandler : IMcpCommandHandler
    {
        private static readonly List<string> LastErrors = new List<string>();
        private static readonly List<string> LastWarnings = new List<string>();
        private static bool _compilationEverFinished = false;

        static CompileCommandHandler()
        {
            CompilationPipeline.compilationStarted += _ =>
            {
                lock (LastErrors)
                {
                    LastErrors.Clear();
                    LastWarnings.Clear();
                }
                _compilationEverFinished = false;
            };

            CompilationPipeline.assemblyCompilationFinished += (path, messages) =>
            {
                lock (LastErrors)
                {
                    foreach (var msg in messages)
                    {
                        var entry = $"{msg.file}({msg.line},{msg.column}): {msg.message}";
                        if (msg.type == CompilerMessageType.Error)
                            LastErrors.Add(entry);
                        else if (msg.type == CompilerMessageType.Warning)
                            LastWarnings.Add(entry);
                    }
                }
            };

            CompilationPipeline.compilationFinished += _ => { _compilationEverFinished = true; };
        }

        public string CommandPrefix => "compile";
        public string Description => "Trigger and monitor Unity script compilation";
        public McpIdempotency Idempotency => McpIdempotency.Unsafe;

        public IReadOnlyList<McpHandlerAction> Actions { get; } = new[]
        {
            new McpHandlerAction("refresh",   McpIdempotency.Unsafe),
            new McpHandlerAction("getStatus", McpIdempotency.Safe),
        };

        public JObject Execute(string action, JObject parameters)
        {
            return action.ToLower() switch
            {
                "refresh"   => TriggerRefresh(),
                "getstatus" => GetStatus(),
                _ => new JObject
                {
                    ["error"] = $"Unknown action: {action}. Supported: refresh, getStatus"
                }
            };
        }

        private static JObject TriggerRefresh()
        {
            AssetDatabase.Refresh();
            return new JObject
            {
                ["triggered"] = true,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["message"] = "AssetDatabase.Refresh() called. If .cs files changed, compilation will begin (brief disconnection during domain reload is normal)."
            };
        }

        private static JObject GetStatus()
        {
            var isCompiling = EditorApplication.isCompiling;
            string[] errors, warnings;
            lock (LastErrors)
            {
                errors = LastErrors.ToArray();
                warnings = LastWarnings.ToArray();
            }

            var status = isCompiling ? "compiling" :
                         errors.Length > 0 ? "errors" : "ready";

            return new JObject
            {
                ["isCompiling"] = isCompiling,
                ["status"] = status,
                ["errorCount"] = errors.Length,
                ["warningCount"] = warnings.Length,
                ["errors"] = new JArray(errors.Cast<object>()),
                ["warnings"] = new JArray(warnings.Cast<object>()),
                ["message"] = status switch
                {
                    "compiling" => "Unity is currently compiling scripts. Poll again until isCompiling=false.",
                    "errors" => $"Compilation finished with {errors.Length} error(s). Fix the errors before running code.",
                    _ => _compilationEverFinished
                        ? "Scripts compiled successfully. Ready to execute."
                        : "No compilation has occurred yet in this session."
                }
            };
        }
    }
}
