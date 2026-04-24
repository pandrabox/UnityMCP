using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityMCP.Editor.Handlers
{
    internal static class CodeExecutor
    {
        private static List<MetadataReference> cachedReferences;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEditor"
        };

        public static JObject Execute(JObject parameters)
        {
            var code = parameters["code"]?.ToString();
            if (string.IsNullOrEmpty(code))
            {
                return new JObject { ["error"] = "Code parameter is required" };
            }

            try
            {
                // Build references on first use
                if (cachedReferences == null)
                {
                    cachedReferences = new List<MetadataReference>();
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                        try
                        {
                            if (File.Exists(asm.Location))
                                cachedReferences.Add(MetadataReference.CreateFromFile(asm.Location));
                        }
                        catch { }
                    }
                }

                // Wrap code
                var usings = string.Join("\n", DefaultUsings.Select(u => $"using {u};"));
                var wrappedCode = $@"
{usings}

namespace McpCodeExecution
{{
    public static class Runner
    {{
        public static object Execute()
        {{
            {code}
            return null;
        }}
    }}
}}";

                // Compile
                var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode,
                    new CSharpParseOptions(LanguageVersion.Latest));

                var compilation = CSharpCompilation.Create(
                    "McpDynamic_" + Guid.NewGuid().ToString("N"),
                    new[] { syntaxTree },
                    cachedReferences,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Release,
                        allowUnsafe: true));

                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.GetMessage())
                        .ToArray();

                    return new JObject
                    {
                        ["error"] = "Compilation failed:\n" + string.Join("\n", errors)
                    };
                }

                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                var type = assembly.GetType("McpCodeExecution.Runner");
                var method = type?.GetMethod("Execute");
                if (method == null)
                {
                    return new JObject { ["error"] = "Failed to find Execute method in compiled code" };
                }

                // Capture Debug.Log output during execution
                var capturedOutput = new StringBuilder();
                Application.LogCallback logHandler = (msg, stackTrace, logType) =>
                {
                    capturedOutput.AppendLine($"[{logType}] {msg}");
                };

                Application.logMessageReceived += logHandler;
                object returnValue;
                try
                {
                    returnValue = method.Invoke(null, null);
                }
                finally
                {
                    Application.logMessageReceived -= logHandler;
                }

                var result = new JObject
                {
                    ["output"] = capturedOutput.ToString().TrimEnd()
                };

                if (returnValue != null)
                {
                    result["returnValue"] = returnValue.ToString();
                }

                return result;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return new JObject
                {
                    ["error"] = $"Runtime error: {inner.Message}"
                };
            }
            catch (Exception e)
            {
                return new JObject
                {
                    ["error"] = $"Error: {e.Message}"
                };
            }
        }
    }
}
