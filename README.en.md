# Unity MCP Integration Framework

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-2.1.0-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6.1-black.svg)
![.NET](https://img.shields.io/badge/.NET-C%23_9.0-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語版](./README.md)

An extensible framework that integrates Unity Editor with the Model Context Protocol (MCP). Claude (and other MCP clients) — or any HTTP client like curl — can drive the Unity Editor directly.

## 🌟 Features (v2.1)

- **HTTP + UDP architecture**: every Unity Editor hosts an HTTP server and announces itself via UDP broadcast for auto-discovery
- **MCP *and* HTTP surfaces**: drive from Claude Desktop / Claude Code via MCP tools, *or* from scripts / CI via curl
- **Multi-Editor support**: run several Unity Editors side-by-side and route by project name via `target` or the HTTP proxy
- **Domain reload resilience**: `SessionState` persists the port so the same port is rebound automatically after a reload
- **Editor panel screenshots** *(Windows)*: capture Inspector / Hierarchy / Project / Console or any EditorWindow by title
- **Built-in code execution**: HTTP `/execute_code` endpoint and MCP tool `unity_execute_code` ship out of the box (Roslyn)
- **Plugin handlers**: implement `IMcpCommandHandler` / `IMcpResourceHandler` / `BasePromptHandler` and they're auto-registered via reflection
- **Unified response envelope**: `{status, result?, error?, truncated?, next?}` for success / error / pagination
- **Context economy**: `limit` / `offset` / `fields` / `detail` to slim down large responses
- **Idempotency classification**: `Safe` / `Unsafe` per-action; TS inspects `err.cause.code` so Unsafe calls never retry after the TCP handshake completes (no double-execution of side effects)

## 📋 Requirements

- Unity 2022.3 or later (including Unity 6000 series)
  - Tested with 2022.3.22f1, 2023.2.19f1, 6000.0.35f1, 6000.1.17f1
- .NET / C# 9.0
- Node.js 18.0.0 or later (for the TypeScript MCP server)
  - Get it from the [Node.js official site](https://nodejs.org/)

## 🚀 Getting Started

### Installation

Install via the Unity Package Manager:

1. Window > Package Manager
2. `+` → **Add package from git URL...**
3. Enter `https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp`

### Quick setup

1. Launching the Unity Editor spawns the HTTP server automatically (127.0.0.1:27182, falling back up to 27199)
2. Open Edit > Preferences > Unity MCP to confirm settings
3. Sanity check: `curl http://127.0.0.1:27182/health`

### Integrating with Claude Desktop / Claude Code

#### Using the installer

1. In the Unity Editor, open Edit > Preferences > Unity MCP
2. Click **Open Installer Window**
3. Follow the installer: confirm Node.js is available, then download the TypeScript client
4. Copy the JSON from the **Configuration Preview** section
5. In Claude Desktop: Settings > Developer > **Edit Config**, paste, and save
6. Restart Claude Desktop

> 💡 **macOS users**: v2.1 detects Homebrew-installed Node at `/opt/homebrew/bin/node` or `/usr/local/bin/node`. This fixes the case where Unity Editor launched from Finder doesn't inherit the shell PATH (#7).

#### Manual setup

1. Clone `unity-mcp-ts` or download the release ZIP
2. Run `npm install && npm run build` to produce `build/index.js`
3. Add this to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "unity-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-mcp-ts/build/index.js"]
    }
  }
}
```

On Windows, escape backslashes (`\\`) or use forward slashes.

### Use it from the CLI (curl)

No TypeScript server required — the Unity Editor's HTTP endpoints are directly callable:

```bash
# Health check
curl http://127.0.0.1:27182/health

# Execute C# code
curl -X POST http://127.0.0.1:27182/execute_code \
  -H "Content-Type: application/json" \
  -d '{"code":"return GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length;"}'

# Screenshot the Inspector (Windows)
curl -X POST http://127.0.0.1:27182/capture_screenshot \
  -H "Content-Type: application/json" \
  -d '{"view":"inspector","maxSize":1024}'
```

For multi-Editor setups, use the TS server's proxy:

```bash
# Discover all running Unity instances (TS server on :27180)
curl http://127.0.0.1:27180/projects

# Route to a specific project by name
curl -X POST http://127.0.0.1:27180/proxy/MyProject/health
```

A Skill at `~/.claude/skills/unity-mcp/` documents common curl workflows.

## 🔌 Architecture (v2.1)

```
MCP client (Claude)
    │ stdio (MCP protocol)
    ▼
unity-mcp-ts (Node)
    ├── HandlerAdapter / HandlerDiscovery  (MCP tools / prompts / resources)
    ├── UnityConnection                     (HTTP fetch + retryableFetch)
    │       ├── sendRequest(cmd, params)    → POST /command
    │       └── sendToEndpoint(path, body)  → POST <path>  (e.g. /execute_code)
    ├── ProjectRegistry                     (UDP :27183, state machine)
    └── ProjectApi :27180-27189             (/projects, /proxy/:name/*)
            │ HTTP
            ▼
Unity Editor(s) — McpHttpServer :27182-27199
    ├── HttpListener + main-thread execution queue
    ├── Built-in shortcuts + plugin handlers
    └── UDP broadcast (27183) every 30s
```

### Unity C# side

- **McpHttpServer**: HTTP listener + UDP broadcaster + main-thread execution queue
- **IMcpCommandHandler** / **IMcpResourceHandler**: plugin extension interfaces (with idempotency)
- **McpIdempotency**: `Safe` / `Unsafe` enum
- **ListResponseBuilder**: shared utility for `limit` / `offset` / `fields`
- **McpEditorInitializer**: `InitializeOnLoad` + `AssemblyReloadEvents`; restores port via SessionState
- **McpHandlerDiscovery**: auto-registers handler classes via reflection

### TypeScript MCP server

- **HandlerAdapter**: registers tools / prompts / resources with the MCP SDK
- **HandlerDiscovery**: scans `src/handlers/` and auto-registers `ICommandHandler` / `IPromptHandler` / `IResourceHandler`
- **UnityConnection**: HTTP client with retry + idempotency + target resolution
- **ProjectRegistry**: UDP receiver + 3-state machine (healthy / reloading / unhealthy)
- **ProjectApi**: 27180-27189 `/projects` + `/proxy/:name/*`
- **retryableFetch**: inspects `err.cause.code` and restricts Unsafe retries to pre-handshake failures

## 📄 MCP Handler Types

| Type | Purpose | MCP control | Interface |
|---|---|---|---|
| Tools (Command) | Perform actions | Model-driven | `IMcpCommandHandler` (C#) / `BaseCommandHandler` (TS) |
| Resources | Provide data | App-driven | `IMcpResourceHandler` (C#) / `BaseResourceHandler` (TS) |
| Prompts | Templates / workflows | User-driven | `BasePromptHandler` (TS only) |

## 📚 Built-in Handlers

### HTTP endpoints (Editor, built-in)

| Endpoint | Idempotency | Description |
|---|---|---|
| `GET /health` | Safe | Version, handler list, uptime |
| `POST /execute_code` | Unsafe | Dynamically compile / run C# via Roslyn |
| `POST /browse_hierarchy` | Safe | Scene hierarchy with filters (supports limit/offset/fields) |
| `POST /inspect` | read/list: Safe, write: Unsafe | Read / write GameObject & component properties |
| `POST /capture_screenshot` | Safe | Game / Scene / Editor panel (inspector / hierarchy / project / console / `window:<title>`) |
| `POST /read_logs` | Safe | Console logs (limit/offset/fields/type) |
| `POST /play_mode` | status: Safe, others: Unsafe | Play Mode control (status/play/stop/pause/unpause/step) |
| `GET /resource` | Safe | Assemblies / packages info |
| `POST /command` | per-command | Plugin handlers (`menu.execute`, `console.*`) |

### MCP tools (TS, built-in)

`unity_listClients`, `unity_setActiveClient`, `unity_connectToProject`, `unity_getActiveClient`, `unity_execute_code`, `console_getLogs`, `console_getCount`, `console_clear`, `console_setFilter`, `menu_execute`

### MCP prompts (TS, built-in)

- `code_execute`: C# code template for `unity_execute_code`

Every tool / endpoint accepts an optional `target` parameter (projectName or clientId) to route requests in multi-Editor setups.

## 🛠️ Writing Custom Handlers

### Command handler (C#)

```csharp
using Newtonsoft.Json.Linq;
using UnityMCP.Editor.Core;

namespace YourNamespace.Handlers
{
    internal sealed class YourCommandHandler : IMcpCommandHandler
    {
        public string CommandPrefix => "yourprefix";
        public string Description => "Handler description";
        public McpIdempotency Idempotency => McpIdempotency.Safe; // use Unsafe for side-effecting actions

        public JObject Execute(string action, JObject parameters)
        {
            if (action == "yourAction")
            {
                return new JObject { ["result"] = "..." };
            }
            // The envelope writer promotes `{error: "..."}` to a proper error response automatically
            return new JObject { ["error"] = $"Unknown action: {action}" };
        }
    }
}
```

### Command handler (TypeScript)

```typescript
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";
import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";

export class YourCommandHandler extends BaseCommandHandler {
    public get commandPrefix(): string { return "yourprefix"; }
    public get description(): string { return "Handler description"; }

    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map();
        tools.set("yourprefix_yourAction", {
            description: "Action description",
            parameterSchema: { param1: z.string() }
        });
        return tools;
    }

    protected async executeCommand(action: string, parameters: JObject): Promise<JObject> {
        return this.sendUnityRequest(`${this.commandPrefix}.${action}`, parameters);
    }
}
```

### Prompt handler (TypeScript)

```typescript
import { BasePromptHandler } from "../core/BasePromptHandler.js";
import { IMcpPromptDefinition } from "../core/interfaces/IPromptHandler.js";

export class YourPromptHandler extends BasePromptHandler {
    public get promptName(): string { return "yourprompt"; }
    public get description(): string { return "Prompt description"; }

    public getPromptDefinitions(): Map<string, IMcpPromptDefinition> {
        const prompts = new Map();
        prompts.set("your-template", {
            description: "Template description",
            template: "Analyse the following code:\n{code}"
        });
        return prompts;
    }
}
```

> 💡 C# handlers can live anywhere in your project — `McpHandlerDiscovery` finds them via reflection. TS handlers under `unity-mcp-ts/src/handlers/` are picked up by `HandlerDiscovery`.

## ⚙️ Configuration

### Unity Editor settings

Edit > Preferences > Unity MCP:

- **HTTP Port**: starting port (default 27182, falls back through 27199)
- **Auto-start on Launch**: start the server when the Editor launches
- **UDP Discovery**: enable UDP broadcasts on port 27183 (default every 30 s)
- **Broadcast Interval**: UDP broadcast cadence
- **Port Persistence**: keep the same port across domain reloads
- **Reload Retry Max MS**: hint for TS / CLI retry cap
- **Detailed Logs**: verbose logging
- **Handler / Resource Enabled States**: per-handler toggles

> ⚠️ v2.1 **removed `Auto-restart on Play Mode Change`**. Play Mode transitions no longer restart the server; the `AssemblyReloadEvents` path handles the reload case automatically.

### TypeScript server environment variables

| Variable | Default | Description |
|---|---|---|
| `MCP_RELOAD_RETRY_MAX_MS` | 15000 | Retry ceiling during domain reloads (ms) |
| `MCP_UNHEALTHY_COOLDOWN_MS` | 60000 | Grace before promoting `reloading` → `unhealthy` |
| `MCP_PROJECT_API_PORT` | 27180 | ProjectApi starting port (27180-27189 fallback) |
| `MCP_UDP_PORT` | 27183 | UDP announce listener port |
| `MCP_HEALTH_INTERVAL` | 10000 | Health-poll interval (ms) |

## 🧪 Tests

- **Unity (EditMode)**: `Editor/Tests/` — 23 cases (ListResponseBuilder / Envelope / Idempotency / ScreenshotCapture)
- **TypeScript (Jest)**: `unity-mcp-ts/src/__tests__/` — 68 cases (UnityConnection / ProjectRegistry / ProjectApi / retry / cache)

```bash
cd unity-mcp-ts
npm test    # expects 68/68 pass
```

## 🔍 Troubleshooting

| Symptom | Remedy |
|---|---|
| Can't reach `/health` | Confirm Unity is running, the package is imported, and something is listening on 27182-27199 |
| `target_required` error | Multiple Unity instances with no `target` specified — pass `target` or call `unity_setActiveClient` |
| Connection drops after domain reload | v2.1 rebinds automatically; if not, check Unity logs for SessionState failures |
| C# handlers not registered | Confirm the class is `public` / `internal` in the Editor assembly, implements `IMcpCommandHandler`, and compiles |
| Node not detected (macOS) | v2.1 adds Homebrew fallback paths. Upgrade to the latest release (#7) |

Detailed error codes are documented in `unity-mcp-ts/README.md` and the [Skill api-reference](~/.claude/skills/unity-mcp/references/api-reference.md).

## 🔒 Security

- **`/execute_code` runs arbitrary C#**. If the endpoint is exposed to anything beyond yourself, disable it via McpSettings or restrict the listener to loopback (v2.x binds 127.0.0.1 only by default).
- **No external exposure**: all HTTP/UDP listeners are loopback-only. LAN exposure is unsupported.

## 📖 External Resources

- [Model Context Protocol (MCP) Specification](https://modelcontextprotocol.io/introduction)
- [unity-mcp-ts README](./unity-mcp-ts/README.md) (TypeScript server details)
- [Unity package README](./jp.shiranui-isuzu.unity-mcp/README.md) (Editor-side details)
- [CHANGELOG](./jp.shiranui-isuzu.unity-mcp/CHANGELOG.md)

## 📄 License

MIT License — see the license file for details.

---

Shiranui-Isuzu
