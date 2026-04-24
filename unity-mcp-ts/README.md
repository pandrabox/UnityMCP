# unity-mcp-ts

TypeScript MCP server that bridges Claude (and other MCP clients) with one or more running Unity Editor instances via HTTP + UDP discovery.

## Architecture

```
MCP client (Claude)
    │ stdio
    ▼
unity-mcp-ts (Node process)
    ├── MCP tool handlers  (HandlerAdapter)
    ├── UnityConnection    (HTTP fetch + retry)
    │       ├── sendRequest(command, params)   → POST /command  (existing tools)
    │       └── sendToEndpoint(path, body)     → POST <path>    (direct endpoints, e.g. /execute_code)
    ├── ProjectRegistry    (UDP :27183 listener, instance state machine)
    └── ProjectApi         (Express :27180, /projects + /proxy/:name/*)
            │ HTTP
            ▼
    Unity Editor(s)        (McpHttpServer :27182–27199)
```

`UnityConnection.sendToEndpoint(path, body, opts)` is the transport for handlers that call non-`/command` endpoints directly (e.g. `unity_execute_code` uses it to POST to `/execute_code`). Custom handlers that extend `BaseCommandHandler` use `sendRequest`; handlers that need a direct path use `sendToEndpoint`.

## Requirements

- Node.js 18 or later
- A Unity project with the `jp.shiranui-isuzu.unity-mcp` package installed

## Installation

```bash
npm install
npm run build
```

Or run directly in development mode (no build step):
```bash
npm run dev
```

## Usage

### As MCP server (Claude Desktop / Claude Code)

Add to your MCP client configuration:

```json
{
  "mcpServers": {
    "unity": {
      "command": "node",
      "args": ["/absolute/path/to/unity-mcp-ts/build/index.js"]
    }
  }
}
```

The server communicates via **stdio** with the MCP client. It discovers Unity Editor instances automatically via UDP broadcast (port 27183). No manual connection setup required.

### As CLI proxy (curl)

The server also exposes an HTTP `ProjectApi` on port 27180 (fallback 27180–27189):

```bash
# List all running Unity instances
curl -s http://127.0.0.1:27180/projects

# Proxy any Editor endpoint by project name
curl -s http://127.0.0.1:27180/proxy/MyProject/health
curl -s -X POST http://127.0.0.1:27180/proxy/MyProject/execute_code \
  -H "Content-Type: application/json" \
  -d '{"code":"return Application.version;"}'
```

## MCP Tools

| Tool | Description |
|---|---|
| `unity_execute_code` | Execute C# code in the Editor main thread (built-in, no setup required) |
| `unity_browse_hierarchy` | Query the scene hierarchy |
| `unity_inspect` | Read or write component properties |
| `unity_capture_screenshot` | Capture Game, Scene, or Editor panel (Windows-only for panels) |
| `unity_read_logs` | Read console logs |
| `unity_play_mode` | Control Play Mode |
| `unity_command` | Execute menu items, console, or asset commands |
| `unity_resource` | Get package or assembly info |
| `unity_listClients` | List all registered Unity instances |
| `unity_setActiveClient` | Set the active instance for subsequent tool calls |
| `unity_getActiveClient` | Get the currently active instance |
| `unity_connectToProject` | Register a Unity instance manually by port |

All tools accept an optional `target` parameter (clientId or project name) to address a specific instance when multiple are registered.

## MCP Prompts

| Prompt | Description |
|---|---|
| `code_execute` | C# code templates for `unity_execute_code` — provides correct boilerplate for code execution requests |

Prompts appear in the MCP client's prompt list (`prompts/list`) and can be fetched via `prompts/get`.

## Multi-Instance Behavior

When multiple Unity Editor instances are running:

1. Each instance broadcasts a UDP announce packet on port 27183 every 30 seconds.
2. The TS server receives these and maintains a `ProjectRegistry` with instance states: `healthy`, `reloading`, or `unhealthy`.
3. Tool calls without a `target` parameter require an explicit active instance to be set (via `unity_setActiveClient`), or exactly one instance must be registered. Otherwise a `target_required` error is returned.

Instance state transitions:
- `healthy` → `reloading`: first poll failure (ECONNREFUSED) after recent contact
- `reloading` → `unhealthy`: consecutive failures for longer than `MCP_UNHEALTHY_COOLDOWN_MS` (default 60s)
- any → `healthy`: successful `/health` response or incoming UDP announce

## Reload Resilience

When Unity performs a domain reload (script edit, Play Mode entry with reload enabled), the HTTP listener goes offline briefly. The TS server:

1. Detects ECONNREFUSED after recent successful contact → marks instance `reloading`
2. Retries with exponential backoff for up to `MCP_RELOAD_RETRY_MAX_MS` (default 15000 ms)
3. Automatically resumes when the Editor comes back online
4. Reports retry progress via `console.error` (visible in MCP client logs)

## Retry Safety (Idempotency)

The server classifies each endpoint as Safe or Unsafe based on `handlers[].idempotency` from the Editor's `/health` response.

- **Safe endpoints** (e.g. `/browse_hierarchy`, `/read_logs`): retried on all transient failures
- **Unsafe endpoints** (e.g. `/execute_code`, `/play_mode:play`, `/play_mode:step`): only retried on TCP pre-handshake failures (`ECONNREFUSED`, `ENOTFOUND`, `UND_ERR_CONNECT_TIMEOUT`) to prevent double-execution. Handler names use `:` separators for per-action idempotency granularity (see `/health.handlers[]`)

For the `/proxy/:name/*` passthrough, classification is based on the sub-path alone (no external header can upgrade idempotency).

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `MCP_RELOAD_RETRY_MAX_MS` | `15000` | Max retry duration (ms) during domain reload |
| `MCP_UNHEALTHY_COOLDOWN_MS` | `60000` | Cooldown (ms) before reloading → unhealthy |
| `MCP_PROJECT_API_PORT` | `27180` | ProjectApi start port (scans 27180–27189) |
| `MCP_UDP_PORT` | `27183` | UDP announce listener port |
| `MCP_HEALTH_INTERVAL` | `10000` | Health poll interval (ms) |

## ProjectApi Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/projects` | GET | List all registered Unity instances |
| `/projects/:name` | GET | Get a single instance by partial name match |
| `/proxy/:name/*` | ANY | Transparent passthrough to the named Unity instance |

### Proxy details

- Body buffered up to **10 MB** (413 if exceeded)
- Hop-by-hop headers stripped; `Host` rewritten to Unity's address
- Retry logic applies (see idempotency above)
- Sub-path idempotency: `/health`, `/read_logs`, `/browse_hierarchy`, `/capture_screenshot`, `/resource` are Safe; all others are Unsafe

## Error Codes

| Code | HTTP | Meaning |
|---|---|---|
| `no_instance` | 503 | No Unity instances registered |
| `unhealthy` | 503 | Target instance is unhealthy |
| `target_not_found` | 404 | `target` matched no instances |
| `target_required` | 400 | Multiple instances, no target specified |
| `multiple_matches` | 409 | Ambiguous project name in proxy path |
| `body_too_large` | 413 | Proxy body > 10 MB |

## Development

```bash
# Run tests
npm test

# Lint
npm run lint
npm run lint:fix

# Build
npm run build
```

## Migration from v2.0 → v2.1

- **`unity_execute_code`** is now a built-in MCP tool. If you imported `UnityMCPHandlerSamplesJS` for code execution, remove it.
- **`code_execute`** MCP prompt is now built-in. Same action: remove any JS sample that provided it.
- No breaking API changes for v2.0 consumers.

## Migration from v1.x

- All responses now use `{"status":"success","result":{...}}` envelope. Parse `.result` instead of the top level.
- `autoRestartOnPlayModeChange` removed — no server restarts on PlayMode transitions.
- Auto-active instance on register removed — multi-instance users must call `unity_setActiveClient` or pass `target`.
- ProjectApi port may now be on 27180–27189 (previously fixed 27180).
