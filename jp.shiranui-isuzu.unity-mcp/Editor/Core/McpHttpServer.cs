using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Resources;
using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// HTTP server that exposes Unity Editor functionality via REST endpoints.
    /// All endpoints return the unified envelope: {status, result, truncated?, next?}.
    /// </summary>
    internal sealed class McpHttpServer : IDisposable
    {
        // Protocol version advertised in /health and UDP announce payloads.
        private const string ProtocolVersion = "2.1.0";

        // ── Built-in endpoint/action idempotency table ──
        // Granular per-action entries exposed via /health.handlers[].
        // Uses `:` as the action separator: "/inspect:write", "/play_mode:step", etc.
        private static readonly (string Name, McpIdempotency Idem)[] BuiltinHandlerEntries =
        {
            ("/health",                McpIdempotency.Safe),
            ("/resource",              McpIdempotency.Safe),
            ("/read_logs",             McpIdempotency.Safe),
            ("/browse_hierarchy",      McpIdempotency.Safe),
            ("/capture_screenshot",    McpIdempotency.Safe),
            ("/inspect:read",          McpIdempotency.Safe),
            ("/inspect:list",          McpIdempotency.Safe),
            ("/inspect:write",         McpIdempotency.Unsafe),
            ("/play_mode:status",      McpIdempotency.Safe),
            ("/play_mode:play",        McpIdempotency.Unsafe),
            ("/play_mode:stop",        McpIdempotency.Unsafe),
            ("/play_mode:pause",       McpIdempotency.Unsafe),
            ("/play_mode:unpause",     McpIdempotency.Unsafe),
            ("/play_mode:step",        McpIdempotency.Unsafe),
            ("/execute_code",          McpIdempotency.Unsafe),
        };

        // HTTP server
        private HttpListener httpListener;
        private Thread listenerThread;
        private int boundPort;
        private bool running;

        // UDP broadcaster
        private Timer broadcastTimer;
        private readonly int broadcastPort;
        private readonly int broadcastIntervalMs;

        // Command & resource handlers
        private readonly Dictionary<string, HandlerRegistration> commandHandlers = new();
        private readonly Dictionary<string, ResourceHandlerRegistration> resourceHandlers = new();
        private readonly Dictionary<string, IMcpResourceHandler> resourceUriMap = new();

        // Main thread queue
        private readonly Queue<Action> mainThreadQueue = new();
        private readonly object queueLock = new();

        private CancellationTokenSource cancellationTokenSource;

        // Request counter (thread-safe)
        private long requestCount;

        // Project info (captured once at construction time)
        private readonly string productName = Application.productName;
        private readonly string unityVersion = Application.unityVersion;
        private readonly string projectPath = Application.dataPath;

        // Events
        public event EventHandler<EventArgs> Started;
        public event EventHandler<EventArgs> Stopped;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        public event EventHandler<ResourceFetchedEventArgs> ResourceFetched;

        /// <summary>Gets whether the server is running.</summary>
        public bool IsRunning => this.running;

        /// <summary>Gets whether the server is listening (alias for IsRunning for HTTP server).</summary>
        public bool IsConnected => this.running;

        /// <summary>Gets the port the server is bound to.</summary>
        public int BoundPort => this.boundPort;

        /// <summary>Gets the server identifier (project name + port).</summary>
        public string ClientId => $"{this.productName}-{this.boundPort}";

        /// <summary>Gets the time when the server was last started.</summary>
        public DateTime ConnectedSince { get; private set; }

        private static bool DetailedLogs => McpSettings.instance.detailedLogs;

        /// <summary>
        /// Creates a new McpHttpServer using settings from McpSettings.
        /// </summary>
        public McpHttpServer()
        {
            var settings = McpSettings.instance;
            this.boundPort = settings.httpPort;
            this.broadcastPort = settings.udpBroadcastPort;
            this.broadcastIntervalMs = settings.broadcastIntervalSeconds * 1000;

            EditorApplication.update += this.ProcessMainThreadQueue;

            if (DetailedLogs)
            {
                Debug.Log($"[McpHttpServer] Initialized, target port={this.boundPort}");
            }
        }

        // ──────────────────────────────────────────────
        //  Lifecycle  (race-free Start per design §2.1)
        // ──────────────────────────────────────────────

        /// <summary>
        /// Starts the HTTP server and UDP broadcaster.
        /// </summary>
        /// <param name="preferredPort">
        /// If provided, attempts to bind this port first before scanning the range.
        /// Defaults to <c>McpSettings.instance.httpPort</c> when null.
        /// </param>
        public void Start(int? preferredPort = null)
        {
            if (this.running) return;

            var startPort = preferredPort ?? McpSettings.instance.httpPort;

            // Step 1: bind listener — throws on total failure, confirms actual port.
            this.boundPort = this.StartHttpListener(startPort);

            // Step 2: persist actual port immediately after successful bind (SessionState).
            SessionState.SetInt("UnityMCP.BoundPort", this.boundPort);
            SessionState.SetBool("UnityMCP.WasRunning", true);

            // Step 3: set state flags BEFORE thread start so ListenerLoop and /health
            //         both observe running=true from the very first iteration.
            this.cancellationTokenSource = new CancellationTokenSource();
            this.ConnectedSince = DateTime.Now;
            this.requestCount = 0;
            this.running = true;  // ← must precede thread start

            // Step 4: start background threads inside try/catch.
            try
            {
                this.listenerThread = new Thread(this.ListenerLoop)
                {
                    IsBackground = true,
                    Name = "McpHttpListenerThread"
                };
                this.listenerThread.Start();

                Debug.Log($"[McpHttpServer] HTTP server listening on http://127.0.0.1:{this.boundPort}/");

                this.StartBroadcaster();
            }
            catch (Exception ex)
            {
                // Roll back — threads/broadcaster failed to start.
                this.running = false;
                try { this.cancellationTokenSource?.Cancel(); this.cancellationTokenSource?.Dispose(); }
                catch { }
                this.cancellationTokenSource = null;
                try { this.httpListener?.Close(); }
                catch { }
                this.httpListener = null;

                // Keep BoundPort as a hint for the next retry attempt; only clear WasRunning.
                SessionState.SetBool("UnityMCP.WasRunning", false);

                Debug.LogError($"[McpHttpServer] Failed to start listener threads: {ex.Message}");
                throw;
            }

            // Step 5: fire Started event.
            this.Started?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Stops the HTTP server and UDP broadcaster.
        /// </summary>
        public void Stop()
        {
            this.running = false;  // signals ListenerLoop to exit
            this.cancellationTokenSource?.Cancel();

            // Stop broadcaster
            this.broadcastTimer?.Dispose();
            this.broadcastTimer = null;

            // Stop listener
            try
            {
                this.httpListener?.Stop();
                this.httpListener?.Close();
            }
            catch (Exception ex)
            {
                if (DetailedLogs) Debug.LogWarning($"[McpHttpServer] Error closing listener: {ex.Message}");
            }
            finally
            {
                this.httpListener = null;
            }

            if (this.listenerThread is { IsAlive: true })
            {
                this.listenerThread.Join(2000);
                this.listenerThread = null;
            }

            // IMPORTANT: Stop() does NOT touch SessionState.
            // Reload path (OnBeforeAssemblyReload) must preserve WasRunning=true,
            // so it writes SessionState before calling Dispose() → Stop().
            // User-initiated stop callers are responsible for persisting WasRunning=false explicitly.

            Debug.Log("[McpHttpServer] Server stopped");
            this.Stopped?.Invoke(this, EventArgs.Empty);
        }

        // ──────────────────────────────────────────────
        //  HTTP Listener
        // ──────────────────────────────────────────────

        private int StartHttpListener(int startPort)
        {
            const int maxPort = 27199;
            for (var port = startPort; port <= maxPort; port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    this.httpListener = listener;
                    return port;
                }
                catch (HttpListenerException)
                {
                    // Port in use — try next
                }
            }

            throw new InvalidOperationException($"No available port in range {startPort}-{maxPort}");
        }

        private void ListenerLoop()
        {
            try
            {
                while (this.running && !this.cancellationTokenSource.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = this.httpListener.GetContext();
                    }
                    catch (HttpListenerException)
                    {
                        break; // Listener was stopped
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    // Handle each request on a thread pool thread
                    ThreadPool.QueueUserWorkItem(_ => this.HandleRequest(context));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception e)
            {
                Debug.LogError($"[McpHttpServer] Listener loop error: {e.Message}");
            }
        }

        // ──────────────────────────────────────────────
        //  Request Routing
        // ──────────────────────────────────────────────

        private void HandleRequest(HttpListenerContext context)
        {
            Interlocked.Increment(ref this.requestCount);

            var request = context.Request;
            var response = context.Response;

            // CORS headers for local development
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            try
            {
                var path = request.Url.AbsolutePath.TrimEnd('/');
                var method = request.HttpMethod;

                if (DetailedLogs)
                {
                    Debug.Log($"[McpHttpServer] {method} {path}");
                }

                switch (path)
                {
                    case "/health":
                        this.HandleHealth(response);
                        break;

                    case "/command" when method == "POST":
                        this.HandleCommand(request, response);
                        break;

                    case "/resource" when method == "GET":
                        this.HandleResource(request, response);
                        break;

                    // ── Built-in shortcuts ──
                    case "/read_logs" when method == "POST":
                        this.HandleBuiltinCommand(request, response, LogReader.ReadLogs);
                        break;

                    case "/execute_code" when method == "POST":
                        this.HandleBuiltinCommand(request, response, CodeExecutor.Execute);
                        break;

                    case "/browse_hierarchy" when method == "POST":
                        this.HandleBuiltinCommand(request, response, SceneHierarchy.Browse);
                        break;

                    case "/capture_screenshot" when method == "POST":
                        this.HandleCaptureScreenshot(request, response);
                        break;

                    case "/play_mode" when method == "POST":
                        this.HandleBuiltinCommand(request, response, PlayModeControl.Control);
                        break;

                    case "/inspect" when method == "POST":
                        this.HandleBuiltinCommand(request, response, InspectorAccess.Access);
                        break;

                    // ── Phase 3-5 stubs ──
                    case "/compile/errors" when method == "GET":
                    case "/compile/status" when method == "GET":
                    case "/hlsl/errors" when method == "GET":
                    case "/test/run" when method == "POST":
                    case "/test/results" when method == "GET":
                    case "/eval" when method == "POST":
                        this.WriteEnvelope(response, 501, null, errorCode: "not_implemented", errorMessage: "Not implemented yet");
                        break;

                    default:
                        this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[McpHttpServer] Request error: {e.Message}");
                try
                {
                    this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: e.Message);
                }
                catch
                {
                    // Response may already be closed
                }
            }
        }

        // ──────────────────────────────────────────────
        //  Built-in Command Handler
        // ──────────────────────────────────────────────

        /// <summary>
        /// Reads and parses the JSON request body. Returns false and writes a 400 error
        /// envelope ("invalid_params" with parse error detail) on malformed JSON.
        /// Empty bodies are treated as an empty JObject (not an error).
        /// </summary>
        private bool TryReadJsonBody(HttpListenerRequest request, HttpListenerResponse response, out JObject parameters)
        {
            string json;
            try
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                json = reader.ReadToEnd();
            }
            catch (Exception ioEx)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Failed to read request body: {ioEx.Message}");
                parameters = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                parameters = new JObject();
                return true;
            }

            try
            {
                parameters = JObject.Parse(json);
                return true;
            }
            catch (JsonReaderException jre)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid JSON: {jre.Message}");
                parameters = null;
                return false;
            }
            catch (Exception e)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid JSON body: {e.Message}");
                parameters = null;
                return false;
            }
        }

        private void HandleBuiltinCommand(
            HttpListenerRequest request,
            HttpListenerResponse response,
            Func<JObject, JObject> handler)
        {
            if (!this.TryReadJsonBody(request, response, out var parameters))
            {
                return;
            }

            JObject result = null;
            Exception executionError = null;
            var waitHandle = new ManualResetEvent(false);

            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    result = handler(parameters);
                }
                catch (Exception e)
                {
                    executionError = e;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            if (!waitHandle.WaitOne(10000))
            {
                this.WriteEnvelope(response, 504, null, errorCode: "timeout", errorMessage: "Timed out waiting for main thread execution");
                return;
            }

            if (executionError != null)
            {
                this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: executionError.Message);
                return;
            }

            this.WriteEnvelope(response, 200, result);
        }

        // ──────────────────────────────────────────────
        //  /capture_screenshot — specialised error handling
        // ──────────────────────────────────────────────

        /// <summary>
        /// Dedicated handler for /capture_screenshot that translates
        /// <see cref="McpScreenshotException"/> into an error envelope with the
        /// handler-supplied code and HTTP status (e.g. window_not_found=400,
        /// unsupported_platform=501).
        /// </summary>
        private void HandleCaptureScreenshot(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!this.TryReadJsonBody(request, response, out var parameters))
            {
                return;
            }

            JObject result = null;
            Exception executionError = null;
            var waitHandle = new ManualResetEvent(false);

            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    result = ScreenshotCapture.Capture(parameters);
                }
                catch (Exception e)
                {
                    executionError = e;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            if (!waitHandle.WaitOne(10000))
            {
                this.WriteEnvelope(response, 504, null, errorCode: "timeout", errorMessage: "Timed out waiting for main thread execution");
                return;
            }

            if (executionError is McpScreenshotException screenshotEx)
            {
                this.WriteEnvelope(response, screenshotEx.HttpStatus, null, errorCode: screenshotEx.Code, errorMessage: screenshotEx.Message);
                return;
            }

            if (executionError != null)
            {
                this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: executionError.Message);
                return;
            }

            this.WriteEnvelope(response, 200, result);
        }

        // ──────────────────────────────────────────────
        //  Endpoint Handlers
        // ──────────────────────────────────────────────

        /// <summary>
        /// /health returns the unified envelope with a `result` payload describing
        /// the server state, available handlers, and idempotency classification.
        /// Per spec R1.4 all responses (including /health) use the envelope.
        /// </summary>
        private void HandleHealth(HttpListenerResponse response)
        {
            var body = this.BuildHealthResponse();
            this.WriteEnvelope(response, 200, body);
        }

        /// <summary>
        /// Builds the /health response payload (goes under the envelope's `result`).
        /// Includes built-in HTTP shortcuts with per-action granularity plus any
        /// registered IMcpCommandHandler entries (prefixed with "/command:").
        /// </summary>
        private JObject BuildHealthResponse()
        {
            var uptimeSec = (DateTime.Now - this.ConnectedSince).TotalSeconds;

            var handlerArray = new JArray();

            // 1. Built-in HTTP shortcuts with per-action granularity.
            foreach (var entry in BuiltinHandlerEntries)
            {
                handlerArray.Add(new JObject
                {
                    ["name"] = entry.Name,
                    ["idempotency"] = entry.Idem.ToString().ToLowerInvariant()
                });
            }

            // 2. Registered IMcpCommandHandler plugins — "/command:<prefix>" or
            //    "/command:<prefix>.<action>" when the handler declares per-action
            //    overrides via IMcpCommandHandler.Actions.
            foreach (var kv in this.commandHandlers)
            {
                var prefix = kv.Key;
                var handler = kv.Value.Handler;
                var actions = handler.Actions;

                if (actions != null && actions.Count > 0)
                {
                    // Emit per-action entries — class-level Idempotency is ignored
                    // because the handler opted into fine-grained declaration.
                    foreach (var a in actions)
                    {
                        handlerArray.Add(new JObject
                        {
                            ["name"] = $"/command:{prefix}.{a.Action}",
                            ["idempotency"] = a.Idempotency.ToString().ToLowerInvariant()
                        });
                    }
                }
                else
                {
                    // Fall back to the class-level Idempotency.
                    handlerArray.Add(new JObject
                    {
                        ["name"] = $"/command:{prefix}",
                        ["idempotency"] = handler.Idempotency.ToString().ToLowerInvariant()
                    });
                }
            }

            var resourceArray = new JArray();
            foreach (var kv in this.resourceHandlers)
            {
                resourceArray.Add(kv.Value.Handler.ResourceUri);
            }

            return new JObject
            {
                ["v"] = ProtocolVersion,
                ["project"] = this.productName,
                ["unity"] = this.unityVersion,
                ["port"] = this.boundPort,
                ["clientId"] = this.ClientId,
                // state is always "running" when the listener can respond (design §2.1)
                ["state"] = "running",
                ["uptimeSec"] = uptimeSec,
                ["reqCount"] = Interlocked.Read(ref this.requestCount),
                ["handlers"] = handlerArray,
                ["resources"] = resourceArray
            };
        }

        private void HandleCommand(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!this.TryReadJsonBody(request, response, out var body))
            {
                return;
            }

            var commandType = body["command"]?.ToString();
            var parameters = body["params"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(commandType))
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: "Missing 'command' field");
                return;
            }

            var parts = commandType.Split('.');
            if (parts.Length < 2)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid command format: {commandType}. Expected: 'prefix.action'");
                return;
            }

            var prefix = parts[0];
            var action = parts[1];

            if (!this.commandHandlers.TryGetValue(prefix, out var registration))
            {
                this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown command prefix: {prefix}");
                return;
            }

            if (!registration.Enabled)
            {
                this.WriteEnvelope(response, 409, null, errorCode: "handler_disabled", errorMessage: $"Command prefix '{prefix}' is disabled");
                return;
            }

            JObject result = null;
            Exception executionError = null;
            var waitHandle = new ManualResetEvent(false);

            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    result = registration.Handler.Execute(action, parameters);
                    this.OnCommandExecuted(new CommandExecutedEventArgs(prefix, action, parameters, result));
                }
                catch (Exception e)
                {
                    executionError = e;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            if (!waitHandle.WaitOne(10000))
            {
                this.WriteEnvelope(response, 504, null, errorCode: "timeout", errorMessage: "Timed out waiting for main thread execution");
                return;
            }

            if (executionError != null)
            {
                this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: executionError.Message);
                return;
            }

            this.WriteEnvelope(response, 200, result);
        }

        private void HandleResource(HttpListenerRequest request, HttpListenerResponse response)
        {
            var resourceName = request.QueryString["name"];
            if (string.IsNullOrEmpty(resourceName))
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: "Missing 'name' query parameter");
                return;
            }

            var parameters = new JObject();
            foreach (string key in request.QueryString)
            {
                if (key != "name")
                    parameters[key] = request.QueryString[key];
            }

            JObject result = null;
            Exception executionError = null;
            var waitHandle = new ManualResetEvent(false);

            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    result = this.FetchResourceData(resourceName, parameters);
                }
                catch (Exception e)
                {
                    executionError = e;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            if (!waitHandle.WaitOne(10000))
            {
                this.WriteEnvelope(response, 504, null, errorCode: "timeout", errorMessage: "Timed out waiting for main thread execution");
                return;
            }

            if (executionError != null)
            {
                this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: executionError.Message);
                return;
            }

            // FetchResourceData may return a result with truncated/next already set by handlers
            this.WriteEnvelope(response, 200, result);
        }

        // ──────────────────────────────────────────────
        //  Unified Envelope Writer (A1)
        // ──────────────────────────────────────────────

        /// <summary>
        /// Writes a unified response envelope:
        ///   Success: {status:"success", result:{...}, truncated?, next?}
        ///   Error:   {status:"error", error:{code, message}}
        /// </summary>
        private void WriteEnvelope(
            HttpListenerResponse response,
            int statusCode,
            JObject result,
            string errorCode = null,
            string errorMessage = null)
        {
            JObject envelope;

            if (errorCode != null || statusCode >= 400)
            {
                envelope = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = errorCode ?? "internal_error",
                        ["message"] = errorMessage ?? "An error occurred"
                    }
                };
            }
            else if (result != null
                     && result["error"] != null
                     && result["error"].Type == JTokenType.String
                     && result["result"] == null)
            {
                // Legacy handler pattern: `{"error": "msg"}` returned from a handler.
                // Promote to proper error envelope so status/HTTP code reflect the failure.
                envelope = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = "invalid_params",
                        ["message"] = result["error"].ToString()
                    }
                };
                statusCode = 400;
            }
            else
            {
                // Hoist truncated/next from the result object if present (set by ListResponseBuilder)
                var truncated = result?["truncated"];
                var next = result?["next"];

                // Build a clean result without the pagination keys at top level
                // (they belong on the envelope, not inside result)
                JObject cleanResult = null;
                if (result != null)
                {
                    cleanResult = new JObject();
                    foreach (var prop in result.Properties())
                    {
                        if (prop.Name == "truncated" || prop.Name == "next")
                            continue;
                        cleanResult[prop.Name] = prop.Value;
                    }
                }

                envelope = new JObject { ["status"] = "success" };
                if (cleanResult != null)
                    envelope["result"] = cleanResult;

                if (truncated != null)
                    envelope["truncated"] = truncated;
                if (next != null && next.Type != JTokenType.Null)
                    envelope["next"] = next;
            }

            this.WriteRaw(response, statusCode, envelope);
        }

        /// <summary>
        /// Writes a raw JObject body to the HTTP response. Internal helper used by
        /// WriteEnvelope after building the unified envelope.
        /// </summary>
        private void WriteRaw(HttpListenerResponse response, int statusCode, JObject body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";

            if (body == null || statusCode == 204)
            {
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            var json = JsonConvert.SerializeObject(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        // ──────────────────────────────────────────────
        //  UDP Broadcaster
        // ──────────────────────────────────────────────

        private void StartBroadcaster()
        {
            this.SendBroadcast();
            this.broadcastTimer = new Timer(
                _ => this.SendBroadcast(),
                null,
                this.broadcastIntervalMs,
                this.broadcastIntervalMs
            );
        }

        private void SendBroadcast()
        {
            try
            {
                using var socket = new UdpClient();
                socket.EnableBroadcast = true;

                var announcement = new JObject
                {
                    ["type"] = "unity_announce",
                    ["n"] = this.productName,
                    ["path"] = this.projectPath,
                    ["port"] = this.boundPort,
                    ["unity"] = this.unityVersion,
                    ["v"] = ProtocolVersion,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(announcement));
                socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, this.broadcastPort));

                if (DetailedLogs)
                {
                    Debug.Log($"[McpHttpServer] UDP broadcast sent on port {this.broadcastPort}");
                }
            }
            catch (Exception e)
            {
                if (DetailedLogs)
                {
                    Debug.LogWarning($"[McpHttpServer] Broadcast error: {e.Message}");
                }
            }
        }

        // ──────────────────────────────────────────────
        //  Main Thread Queue
        // ──────────────────────────────────────────────

        private void ProcessMainThreadQueue()
        {
            lock (this.queueLock)
            {
                while (this.mainThreadQueue.Count > 0)
                {
                    var action = this.mainThreadQueue.Dequeue();
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[McpHttpServer] Main thread action error: {e.Message}");
                    }
                }
            }
        }

        private void ExecuteOnMainThread(Action action)
        {
            lock (this.queueLock)
            {
                this.mainThreadQueue.Enqueue(action);
            }
        }

        // ──────────────────────────────────────────────
        //  Handler Registration
        // ──────────────────────────────────────────────

        /// <summary>Registers a command handler.</summary>
        public void RegisterHandler(IMcpCommandHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("[McpHttpServer] Cannot register null handler");
                return;
            }

            var commandPrefix = handler.CommandPrefix;
            if (string.IsNullOrEmpty(commandPrefix))
            {
                Debug.LogError($"[McpHttpServer] Handler {handler.GetType().Name} has invalid command prefix");
                return;
            }

            if (this.commandHandlers.ContainsKey(commandPrefix))
            {
                Debug.LogWarning($"[McpHttpServer] Replacing existing handler for '{commandPrefix}'");
            }

            if (McpSettings.instance.handlerEnabledStates.TryGetValue(commandPrefix, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            this.commandHandlers[commandPrefix] = new HandlerRegistration(handler, enabled);
            Debug.Log($"[McpHttpServer] Registered command handler: {commandPrefix} (Enabled: {enabled})");

            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);
        }

        /// <summary>Enables or disables a command handler.</summary>
        public bool SetHandlerEnabled(string commandPrefix, bool enabled)
        {
            if (!this.commandHandlers.TryGetValue(commandPrefix, out var registration))
            {
                Debug.LogWarning($"[McpHttpServer] Handler '{commandPrefix}' not found");
                return false;
            }

            registration.Enabled = enabled;
            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);
            return true;
        }

        /// <summary>Gets all registered command handlers.</summary>
        public IReadOnlyDictionary<string, HandlerRegistration> GetRegisteredHandlers()
        {
            return this.commandHandlers;
        }

        /// <summary>Registers a resource handler.</summary>
        public bool RegisterResourceHandler(IMcpResourceHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("[McpHttpServer] Cannot register null resource handler");
                return false;
            }

            var resourceName = handler.ResourceName;
            if (string.IsNullOrEmpty(resourceName))
            {
                Debug.LogError($"[McpHttpServer] Handler {handler.GetType().Name} has invalid resource name");
                return false;
            }

            if (this.resourceHandlers.ContainsKey(resourceName))
            {
                Debug.LogWarning($"[McpHttpServer] Replacing existing resource handler '{resourceName}'");
                this.resourceHandlers.Remove(resourceName);
            }

            if (McpSettings.instance.resourceHandlerEnabledStates.TryGetValue(resourceName, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            this.resourceHandlers[resourceName] = new ResourceHandlerRegistration(handler, enabled);

            if (!string.IsNullOrEmpty(handler.ResourceUri))
            {
                this.resourceUriMap[handler.ResourceUri] = handler;
            }

            Debug.Log($"[McpHttpServer] Registered resource handler: {resourceName} (Enabled: {enabled})");
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);

            return true;
        }

        /// <summary>Enables or disables a resource handler.</summary>
        public bool SetResourceHandlerEnabled(string resourceName, bool enabled)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                Debug.LogWarning($"[McpHttpServer] Resource handler '{resourceName}' not found");
                return false;
            }

            registration.Enabled = enabled;
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);
            return true;
        }

        /// <summary>Gets all registered resource handlers.</summary>
        public IReadOnlyDictionary<string, ResourceHandlerRegistration> GetRegisteredResourceHandlers()
        {
            return this.resourceHandlers;
        }

        // ──────────────────────────────────────────────
        //  Resource Fetching
        // ──────────────────────────────────────────────

        private JObject FetchResourceData(string resourceName, JObject parameters)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                // Return an error payload — WriteEnvelope will detect the missing result and treat as error
                throw new InvalidOperationException($"Resource not found: {resourceName}");
            }

            if (!registration.Enabled)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' is disabled");
            }

            var result = registration.Handler.FetchResource(parameters);
            this.OnResourceFetched(new ResourceFetchedEventArgs(resourceName, parameters, result));
            return result;
        }

        // ──────────────────────────────────────────────
        //  Events
        // ──────────────────────────────────────────────

        private void OnCommandExecuted(CommandExecutedEventArgs e) => this.CommandExecuted?.Invoke(this, e);
        private void OnResourceFetched(ResourceFetchedEventArgs e) => this.ResourceFetched?.Invoke(this, e);

        // ──────────────────────────────────────────────
        //  IDisposable
        // ──────────────────────────────────────────────

        public void Dispose()
        {
            this.Stop();
            EditorApplication.update -= this.ProcessMainThreadQueue;
            GC.SuppressFinalize(this);
        }

        // ──────────────────────────────────────────────
        //  Inner Types
        // ──────────────────────────────────────────────

        public class HandlerRegistration
        {
            public IMcpCommandHandler Handler { get; }
            public bool Enabled { get; set; }
            public string Description => this.Handler.Description;
            public string AssemblyName { get; }

            public HandlerRegistration(IMcpCommandHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        public class ResourceHandlerRegistration
        {
            public IMcpResourceHandler Handler { get; }
            public bool Enabled { get; set; }
            public string Description => this.Handler.Description;
            public string AssemblyName { get; }

            public ResourceHandlerRegistration(IMcpResourceHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        public class CommandExecutedEventArgs : EventArgs
        {
            public string Prefix { get; }
            public string Action { get; }
            public JObject Parameters { get; }
            public JObject Result { get; }

            public CommandExecutedEventArgs(string prefix, string action, JObject parameters, JObject result)
            {
                this.Prefix = prefix;
                this.Action = action;
                this.Parameters = parameters;
                this.Result = result;
            }
        }

        public class ResourceFetchedEventArgs : EventArgs
        {
            public string ResourceName { get; }
            public JObject Parameters { get; }
            public JObject Result { get; }

            public ResourceFetchedEventArgs(string resourceName, JObject parameters, JObject result)
            {
                this.ResourceName = resourceName;
                this.Parameters = parameters;
                this.Result = result;
            }
        }
    }
}
