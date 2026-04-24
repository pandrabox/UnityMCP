# Unity MCP

A Unity Editor package that exposes an HTTP API for AI-driven automation via the Model Context Protocol (MCP). Claude and other MCP clients can execute C# code, inspect the scene hierarchy, control Play Mode, capture screenshots, read console logs, and more — all without leaving the editor.

## Architecture

```
 ┌──────────────────────────┐       stdio        ┌────────────────────────────┐
 │   MCP client (Claude)    │ ─────────────────▶ │   TS MCP server            │
 └──────────────────────────┘                    │   (unity-mcp-ts/dist)      │
                                                 │                            │
 ┌──────────────────────────┐       HTTP         │  UnityConnection           │
 │  CLI (curl / Skill)      │ ─────────────────▶ │  ProjectRegistry (UDP)     │
 └──────────────────────────┘    :27180 (proxy)  │  ProjectApi :27180         │
                                                 └────────────┬───────────────┘
                                                      HTTP /command, /inspect etc.
                                                              ▼
 ┌──────────────────────────┐       UDP          ┌────────────────────────────┐
 │ Unity Editor (A)         │ ────── :27183 ───▶ │  (broadcast received by TS)│
 │  McpHttpServer :27182    │                    └────────────────────────────┘
 │  Handlers + Resources    │
 └──────────────────────────┘
 ┌──────────────────────────┐
 │ Unity Editor (B)         │  (same UDP :27183 broadcast)
 │  McpHttpServer :27183    │
 └──────────────────────────┘
```

**Two connection paths:**

- **Direct** — `curl http://127.0.0.1:27182/...` (no TS server needed, single-editor quickpath)
- **Proxy** — `curl http://127.0.0.1:27180/proxy/<name>/...` (multi-project routing, automatic reload retry)

## Requirements

- Unity 2022.3 or later
- [Newtonsoft Json for Unity](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html) (com.unity.nuget.newtonsoft-json 3.2.1) — added automatically

## Installation

### Via Unity Package Manager (UPM)

1. Open **Window > Package Manager**
2. Click **+** → **Add package from git URL**
3. Enter: `https://github.com/shiranui-isuzu/unity-mcp.git?path=jp.shiranui-isuzu.unity-mcp`

### Manual

Download or clone the repository and add the `jp.shiranui-isuzu.unity-mcp` folder to your project's `Packages/` directory.

## Quick Start

### With MCP client (Claude Desktop / Claude Code)

1. Install and start the TS MCP server (see `unity-mcp-ts/README.md`)
2. Add to your MCP client config:
   ```json
   {
     "mcpServers": {
       "unity": {
         "command": "node",
         "args": ["/path/to/unity-mcp-ts/build/index.js"]
       }
     }
   }
   ```
3. Open Unity — the MCP server starts automatically (if `autoStartOnLaunch = true`)
4. In Claude, use tools like `unity_execute_code`, `unity_browse_hierarchy`, etc.

### With CLI (curl)

```bash
# Check Unity is running
curl -s http://127.0.0.1:27182/health

# Run C# code
curl -s -X POST http://127.0.0.1:27182/execute_code \
  -H "Content-Type: application/json" \
  -d '{"code":"return Camera.main.name;"}'
# {"status":"success","result":{"output":"","returnValue":"Main Camera"}}
```

See the [CLI Skill](https://github.com/shiranui-isuzu/unity-mcp) for a full reference.

## Endpoints

| Endpoint | Method | Idempotency | Description |
|---|---|---|---|
| `/health` | GET | Safe | Server status, handlers, resources |
| `/execute_code` | POST | Unsafe | Run C# (dynamic compilation) |
| `/browse_hierarchy` | POST | Safe | Query scene hierarchy |
| `/inspect` | POST | Safe/Unsafe | Read/write component properties |
| `/capture_screenshot` | POST | Safe | Game, Scene, or Editor panel screenshot (Windows-only for panels) |
| `/read_logs` | POST | Safe | Console logs with filtering |
| `/play_mode` | POST | Safe/Unsafe | Play/stop/pause/step control |
| `/command` | POST | Per-command | Menu, console, asset commands |
| `/resource` | GET | Safe | Project packages / assemblies |

All responses use the unified envelope: `{"status":"success"|"error","result?:{},"error?:{},"truncated?:bool,"next?:{}}`.

List-returning endpoints (`/read_logs`, `/browse_hierarchy`, `/inspect` list, `/resource`) accept `limit`, `offset`, and `fields` parameters for context-efficient pagination.

### `/capture_screenshot` — view options (v2.1)

The `view` parameter accepts:

| Value | Description |
|---|---|
| `"game"` | Camera render (cross-platform) |
| `"scene"` | Scene view camera render (cross-platform) |
| `"inspector"` | Inspector EditorWindow (Windows only) |
| `"hierarchy"` | Hierarchy EditorWindow (Windows only) |
| `"project"` | Project Browser EditorWindow (Windows only) |
| `"console"` | Console EditorWindow (Windows only) |
| `"game_view_window"` | Game view panel (Windows only) |
| `"scene_view_window"` | Scene view panel (Windows only) |
| `"window:<title>"` | Any EditorWindow matched by title substring, case-insensitive (Windows only) |

Editor panel capture (`inspector`, `hierarchy`, `project`, `console`, `game_view_window`, `scene_view_window`, `window:<title>`) uses Windows P/Invoke (desktop DC + BitBlt + DPI correction). Non-Windows returns `unsupported_platform` (501). The `"game"` and `"scene"` camera-based paths remain cross-platform.

Response includes `windowTitle` alongside `image`, `view`, `width`, `height` when an EditorWindow is captured.

## Settings

Open **Edit > Preferences > Unity MCP** (or **Project Settings > Unity MCP**).

| Setting | Default | Description |
|---|---|---|
| `httpPort` | `27182` | Base HTTP port. Scans 27182–27199 for first available. |
| `autoStartOnLaunch` | `true` | Start server automatically when Unity opens |
| `detailedLogs` | `true` | Log each request with timestamp and latency |
| `useUdpBroadcast` | `true` | Broadcast UDP announce packets for TS server discovery |
| `udpBroadcastPort` | `27183` | UDP broadcast destination port |
| `broadcastIntervalSeconds` | `30` | Interval between UDP announce packets |
| `portPersistenceEnabled` | `true` | Persist the bound port across domain reloads via SessionState |
| `reloadRetryMaxMs` | `15000` | Advisory value for TS/CLI clients: max retry duration during reload |

> **Removed in v2.0:** `autoRestartOnPlayModeChange` — PlayMode transitions no longer restart the server. Domain reload continuity is handled via `AssemblyReloadEvents` only.

### Handler enable/disable

Individual handlers (e.g. `execute_code`) can be enabled or disabled in the settings UI. When disabled, the endpoint returns `{"status":"error","error":{"code":"handler_not_found",...}}`.

## Multi-Editor Setup

You can run multiple Unity Editor instances simultaneously. Each instance binds the next available port (27182, 27183, …).

```bash
# Discover all running instances (TS server must be running)
curl -s http://127.0.0.1:27180/projects
# [
#   {"name":"ProjectA","port":27182,"state":"healthy","clientId":"ProjectA-27182"},
#   {"name":"ProjectB","port":27183,"state":"healthy","clientId":"ProjectB-27183"}
# ]

# Send commands to each instance by name via proxy
curl -s -X POST http://127.0.0.1:27180/proxy/ProjectA/play_mode \
  -H "Content-Type: application/json" \
  -d '{"action":"status"}'

curl -s -X POST http://127.0.0.1:27180/proxy/ProjectB/read_logs \
  -H "Content-Type: application/json" \
  -d '{"limit":5}'
```

In MCP tool calls, use the `target` parameter to specify which instance to address:
```
unity_execute_code(code="...", target="ProjectA")
unity_browse_hierarchy(target="ProjectB")
```

## Domain Reload Continuity

When you edit scripts (or enter Play Mode with domain reload enabled), the C# domain reloads. In v2.0:

- **The server port is preserved** across reloads via `SessionState`. The same port is re-bound after reload completes.
- **The TS server retries automatically** during the reload window (ECONNREFUSED → exponential backoff for up to `reloadRetryMaxMs` ms).
- **PlayMode transitions do not restart the server.** Only actual domain reloads cause a brief offline period.

For direct CLI access during reload:
```bash
curl --retry 5 --retry-connrefused --retry-delay 2 -s http://127.0.0.1:27182/health
```

## v2.1 Notes

### Code execution — now built-in end-to-end

C# code execution is available out-of-the-box via two paths — no samples needed:

- **HTTP**: `POST /execute_code` (has been built-in since v2.0)
- **MCP tool**: `unity_execute_code` (new in v2.1 — callable from Claude Desktop / Claude Code without any additional setup)
- **MCP prompt**: `code_execute` (new in v2.1 — provides C# code templates in MCP clients)

The `UnityMCPHandlerSamples` (C#) and `UnityMCPHandlerSamplesJS` (JS) sample packages have been removed in v2.1 as they are fully superseded by these built-in alternatives. If you previously imported either sample, remove it — no code migration is required.

### Editor panel screenshots — Windows only in v2.1

`/capture_screenshot` now supports Inspector, Hierarchy, Project, Console, and any other EditorWindow via the new `view` values. See the [view options table](#capture_screenshot--view-options-v21) above. Non-Windows returns `unsupported_platform` (501). Camera-based `view=game` / `view=scene` remain cross-platform.

---

## Migration Guide: v1.x → v2.0

### Breaking changes

1. **Unified response envelope**: All endpoints now return `{"status":..., "result":...}`. Previously some endpoints returned flat JSON (e.g. `{"output":"...","returnValue":"..."}`). Update any code that reads top-level keys:
   - `/execute_code`: `.output` → `.result.output`, `.returnValue` → `.result.returnValue`
   - `/capture_screenshot`: `.image` → `.result.image`
   - `/play_mode`: `.isPlaying` → `.result.isPlaying`
   - `/read_logs`: `.logs` → `.result.logs`
   - All other endpoints: wrap your field access with `.result.`

2. **`autoRestartOnPlayModeChange` removed**: Remove this key from any stored settings or config files.

3. **Auto-active instance selection removed**: Previously the first discovered Unity instance was automatically set as active. Now:
   - Single instance: still used automatically (convenience case)
   - Multiple instances + no `target` + no explicit active: returns `target_required` error
   - Fix: call `unity_setActiveClient` in your session, or pass `target` parameter in each tool call

4. **ProjectApi port range**: Now uses 27180–27189 fallback range instead of fixed 27180.

### curl example updates

Before (v1.x):
```bash
curl -s -X POST http://127.0.0.1:27182/execute_code \
  -d '{"code":"return 1;"}' | jq '.returnValue'
```

After (v2.0):
```bash
curl -s -X POST http://127.0.0.1:27182/execute_code \
  -H "Content-Type: application/json" \
  -d '{"code":"return 1;"}' | jq '.result.returnValue'
```

## Security

- The HTTP server binds to `127.0.0.1` only. It is not accessible from the network.
- `/execute_code` can be disabled in settings if you do not want arbitrary C# execution.
- No authentication token is required (loopback-only design assumption).

## License

MIT
