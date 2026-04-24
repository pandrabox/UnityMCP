# Unity MCP 統合フレームワーク

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Version](https://img.shields.io/badge/version-2.1.0-brightgreen)
![Unity](https://img.shields.io/badge/Unity-2022.3%E2%80%93Unity6.1-black.svg)
![.NET](https://img.shields.io/badge/.NET-C%23_9.0-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[English Version](./README.en.md)

Unity Editor と Model Context Protocol (MCP) を統合する拡張フレームワークです。Claude などの AI 言語モデル、または CLI (curl) から、HTTP 経由で Unity Editor を直接操作できます。

## 🌟 特徴 (v2.1)

- **HTTP + UDP アーキテクチャ**: 各 Unity Editor が HTTP サーバを持ち、UDP ブロードキャストで自動 discovery
- **MCP と HTTP の両方をサポート**: Claude Desktop / Claude Code からは MCP tool 経由、スクリプト / CI からは curl 直叩き
- **マルチ Editor 対応**: 複数 Unity Editor を同時起動しても `target` パラメータ or プロキシで名前指定ルーティング
- **ドメインリロード耐性**: `SessionState` で port を永続化し、リロード跨ぎで同 port を自動再バインド
- **Editor パネルキャプチャ** *(Windows)*: Inspector / Hierarchy / Project / Console などの任意 EditorWindow をスクリーンショット
- **built-in コード実行**: HTTP `/execute_code` と MCP tool `unity_execute_code` が標準装備 (Roslyn 使用)
- **拡張可能なプラグインアーキテクチャ**: `IMcpCommandHandler` / `IMcpResourceHandler` / `BasePromptHandler` を実装すればリフレクションで自動登録
- **統一レスポンスエンベロープ**: `{status, result?, error?, truncated?, next?}` で成功/エラー/ページングを一貫した形で返す
- **コンテキスト経済**: `limit` / `offset` / `fields` / `detail` パラメータでレスポンスを絞り込み可能
- **冪等性分類**: `Safe` / `Unsafe` を per-action で宣言し、TS 側が `err.cause.code` を見て再送可否を制御 (副作用操作の二重実行を構造的に排除)

## 📋 必要条件

- Unity 2022.3 以上 (Unity 6000 系対応)
  - 2022.3.22f1、2023.2.19f1、6000.0.35f1、6000.1.17f1 で動作確認
- .NET / C# 9.0
- Node.js 18.0.0 以上 (TypeScript MCP サーバ用)
  - [Node.js 公式サイト](https://nodejs.org/) から入手

## 🚀 はじめに

### インストール方法

Unity パッケージマネージャからインストール:

1. Window > Package Manager を開く
2. 「+」 → 「Add package from git URL...」
3. `https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp` を入力

### クイックセットアップ

1. Unity Editor を起動すると、`McpEditorInitializer` が自動的に HTTP サーバを立ち上げます (127.0.0.1:27182、27182-27199 でフォールバック)
2. Edit > Preferences > Unity MCP で設定を確認
3. `curl http://127.0.0.1:27182/health` で動作確認

### Claude Desktop / Claude Code との連携

#### インストーラーを使う場合

1. Unity Editor で Edit > Preferences > Unity MCP を開く
2. 「Open Installer Window」をクリック
3. インストーラーの指示に従い、Node.js の存在確認後、TypeScript クライアントをダウンロード
4. 「Configuration Preview」セクションの JSON をクリップボードへコピー
5. Claude Desktop の Settings > Developer > Edit Config で貼り付けて保存
6. Claude Desktop を再起動

> 💡 **macOS 利用者へ**: v2.1 で Homebrew 経由の Node (`/opt/homebrew/bin/node`、`/usr/local/bin/node`) の検出に対応しました。Finder から起動した Unity が PATH を継承しない環境でも動作します (#7)。

#### 手動でインストールする場合

1. `unity-mcp-ts` リポジトリをクローン or リリース ZIP を取得
2. `npm install && npm run build` を実行して `build/index.js` を生成
3. Claude Desktop の `claude_desktop_config.json` に追加:

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

Windows ではパスのバックスラッシュをエスケープ (`\\`) するか、フォワードスラッシュを使ってください。

### CLI (curl) でも使える

TypeScript サーバ不要で、HTTP 直叩きから操作可能:

```bash
# ヘルスチェック
curl http://127.0.0.1:27182/health

# C# コード実行
curl -X POST http://127.0.0.1:27182/execute_code \
  -H "Content-Type: application/json" \
  -d '{"code":"return GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length;"}'

# Inspector のスクショ (Windows)
curl -X POST http://127.0.0.1:27182/capture_screenshot \
  -H "Content-Type: application/json" \
  -d '{"view":"inspector","maxSize":1024}'
```

マルチ Editor 用にプロキシ経由の例:

```bash
# 複数 Unity が起動中なら TS サーバの :27180 で discover
curl http://127.0.0.1:27180/projects

# プロジェクト名指定でリクエスト転送
curl -X POST http://127.0.0.1:27180/proxy/MyProject/health
```

Skill として `~/.claude/skills/unity-mcp/` に curl ワークフロー集を同梱しています。

## 🔌 アーキテクチャ (v2.1)

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

### Unity C# プラグイン

- **McpHttpServer**: HTTP リスナー + UDP ブロードキャスタ + メインスレッド実行キュー
- **IMcpCommandHandler** / **IMcpResourceHandler**: プラグイン拡張用インターフェース (Idempotency 付き)
- **McpIdempotency**: `Safe` / `Unsafe` enum
- **ListResponseBuilder**: `limit` / `offset` / `fields` を処理する共通ユーティリティ
- **McpEditorInitializer**: `InitializeOnLoad` + `AssemblyReloadEvents` で SessionState 経由 port 復元
- **McpHandlerDiscovery**: リフレクションでハンドラー自動登録

### TypeScript MCP サーバ

- **HandlerAdapter**: MCP SDK に tools / prompts / resources を登録
- **HandlerDiscovery**: `src/handlers/` を走査して `ICommandHandler` / `IPromptHandler` / `IResourceHandler` を自動登録
- **UnityConnection**: HTTP クライアント (retry + idempotency + target 解決)
- **ProjectRegistry**: UDP 受信 + 3 値ステートマシン (healthy / reloading / unhealthy)
- **ProjectApi**: 27180-27189 の `/projects` + `/proxy/:name/*`
- **retryableFetch**: `err.cause.code` ベースで Unsafe は pre-handshake のみリトライ

## 📄 MCP ハンドラータイプ

| 種別 | 用途 | MCP 制御 | 実装インターフェース |
|---|---|---|---|
| Tools (Command) | アクション実行 | モデル制御 | `IMcpCommandHandler` (C#) / `BaseCommandHandler` (TS) |
| Resources | データ提供 | アプリ制御 | `IMcpResourceHandler` (C#) / `BaseResourceHandler` (TS) |
| Prompts | テンプレ / ワークフロー | ユーザ制御 | `BasePromptHandler` (TS のみ) |

## 📚 組み込みハンドラー

### HTTP エンドポイント (Editor 側、built-in)

| Endpoint | Idempotency | 概要 |
|---|---|---|
| `GET /health` | Safe | バージョン、ハンドラー一覧、稼働時間 |
| `POST /execute_code` | Unsafe | Roslyn で C# を動的コンパイル・実行 |
| `POST /browse_hierarchy` | Safe | シーン階層をフィルタ付きで取得 (limit/offset/fields 対応) |
| `POST /inspect` | read/list: Safe、write: Unsafe | GameObject / Component のプロパティ読み書き |
| `POST /capture_screenshot` | Safe | Game / Scene / Editor パネル (inspector / hierarchy / project / console / `window:<title>`) のキャプチャ |
| `POST /read_logs` | Safe | Console ログを取得 (limit/offset/fields/type) |
| `POST /play_mode` | status: Safe、他: Unsafe | Play Mode 制御 (status/play/stop/pause/unpause/step) |
| `GET /resource` | Safe | assemblies / packages 情報 |
| `POST /command` | per-command | プラグイン系 (`menu.execute`、`console.*`) |

### MCP tools (TS 側、built-in)

`unity_listClients`、`unity_setActiveClient`、`unity_connectToProject`、`unity_getActiveClient`、`unity_execute_code`、`console_getLogs`、`console_getCount`、`console_clear`、`console_setFilter`、`menu_execute`

### MCP prompts (TS 側、built-in)

- `code_execute`: `unity_execute_code` 用の C# コードテンプレート

すべての tool / endpoint は任意で `target` パラメータ (projectName or clientId) を受け、複数 Editor 環境でルーティングを明示できます。

## 🛠️ カスタムハンドラーの作成

### コマンドハンドラー (C#)

```csharp
using Newtonsoft.Json.Linq;
using UnityMCP.Editor.Core;

namespace YourNamespace.Handlers
{
    internal sealed class YourCommandHandler : IMcpCommandHandler
    {
        public string CommandPrefix => "yourprefix";
        public string Description => "ハンドラーの説明";
        public McpIdempotency Idempotency => McpIdempotency.Safe; // Unsafe なら明示

        public JObject Execute(string action, JObject parameters)
        {
            if (action == "yourAction")
            {
                return new JObject { ["result"] = "..." };
            }
            // エンベロープ側で自動的に error envelope に promote される
            return new JObject { ["error"] = $"Unknown action: {action}" };
        }
    }
}
```

### コマンドハンドラー (TypeScript)

```typescript
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";
import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";

export class YourCommandHandler extends BaseCommandHandler {
    public get commandPrefix(): string { return "yourprefix"; }
    public get description(): string { return "ハンドラーの説明"; }

    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map();
        tools.set("yourprefix_yourAction", {
            description: "アクションの説明",
            parameterSchema: { param1: z.string() }
        });
        return tools;
    }

    protected async executeCommand(action: string, parameters: JObject): Promise<JObject> {
        return this.sendUnityRequest(`${this.commandPrefix}.${action}`, parameters);
    }
}
```

### プロンプトハンドラー (TypeScript)

```typescript
import { BasePromptHandler } from "../core/BasePromptHandler.js";
import { IMcpPromptDefinition } from "../core/interfaces/IPromptHandler.js";

export class YourPromptHandler extends BasePromptHandler {
    public get promptName(): string { return "yourprompt"; }
    public get description(): string { return "プロンプトの説明"; }

    public getPromptDefinitions(): Map<string, IMcpPromptDefinition> {
        const prompts = new Map();
        prompts.set("your-template", {
            description: "テンプレートの説明",
            template: "以下のコードを分析してください:\n{code}"
        });
        return prompts;
    }
}
```

> 💡 C# ハンドラーはプロジェクト内のどこに置いても `McpHandlerDiscovery` が自動検出します。TS は `unity-mcp-ts/src/handlers/` に置けば `HandlerDiscovery` が自動登録します。

## ⚙️ 設定

### Unity Editor 設定

Edit > Preferences > Unity MCP:

- **HTTP Port**: サーバ開始ポート (既定 27182、27182-27199 で先着フォールバック)
- **Auto-start on Launch**: Editor 起動時に自動開始
- **UDP Discovery**: UDP ブロードキャスト (ポート 27183、既定 30 秒間隔) の有効化
- **Broadcast Interval**: UDP 送信間隔
- **Port Persistence**: ドメインリロード跨ぎで同じ port を維持
- **Reload Retry Max MS**: TS/CLI 側のリトライ上限のヒント
- **Detailed Logs**: デバッグログの出力切替
- **Handler / Resource Enabled States**: ハンドラーごとの有効化トグル

> ⚠️ v2.1 で **`Auto-restart on Play Mode Change` を削除**しました。Play Mode 遷移はドメインリロードを伴う場合のみ server を Stop/Start し、`AssemblyReloadEvents` 経由で自動復元します。

### TypeScript サーバ環境変数

| Variable | 既定 | 説明 |
|---|---|---|
| `MCP_RELOAD_RETRY_MAX_MS` | 15000 | ドメインリロード中の再試行時間上限 (ms) |
| `MCP_UNHEALTHY_COOLDOWN_MS` | 60000 | reloading → unhealthy への昇格までの猶予 |
| `MCP_PROJECT_API_PORT` | 27180 | ProjectApi 開始ポート (27180-27189 フォールバック) |
| `MCP_UDP_PORT` | 27183 | UDP announce 受信ポート |
| `MCP_HEALTH_INTERVAL` | 10000 | ヘルスポーリング間隔 (ms) |

## 🧪 テスト

- **Unity (EditMode)**: `Editor/Tests/` — 23 ケース (ListResponseBuilder / Envelope / Idempotency / ScreenshotCapture)
- **TS (Jest)**: `unity-mcp-ts/src/__tests__/` — 68 ケース (UnityConnection / ProjectRegistry / ProjectApi / retry / cache)

```bash
cd unity-mcp-ts
npm test    # Jest 68/68 pass 期待
```

## 🔍 トラブルシューティング

| 症状 | 対応 |
|---|---|
| `/health` に接続できない | Unity Editor が起動しているか、MCP パッケージが import されているか、27182-27199 のいずれかが listen しているか確認 |
| `target_required` エラー | 複数 Unity 起動中 + `target` 未指定。`unity_setActiveClient` か `target` パラメータで明示 |
| ドメインリロード後に切れる | v2.1 では自動再バインド。`SessionState` が機能していない場合は Unity ログ確認 |
| C# ハンドラーが登録されない | Editor アセンブリで internal/public、`IMcpCommandHandler` 実装、コンパイルエラー無しを確認 |
| Node が検出されない (Mac) | v2.1 で Homebrew パスにフォールバック対応。最新版を利用 (#7) |

詳細なエラーコードは `unity-mcp-ts/README.md` または [Skill api-reference.md](~/.claude/skills/unity-mcp/references/api-reference.md) 参照。

## 🔒 セキュリティ

- **`/execute_code` は任意の C# を実行できます**。不特定多数がアクセスできる環境では McpSettings から無効化するか、listener を loopback のみに制限してください (v2.x は既定で 127.0.0.1 のみ bind)。
- **外部ネットワーク非公開**: HTTP/UDP すべて loopback 限定。LAN 公開はサポート外です。

## 📖 外部リソース

- [Model Context Protocol (MCP) 仕様](https://modelcontextprotocol.io/introduction)
- [unity-mcp-ts README](./unity-mcp-ts/README.md) (TS サーバ詳細)
- [Unity パッケージ README](./jp.shiranui-isuzu.unity-mcp/README.md) (Editor 側詳細)
- [CHANGELOG](./jp.shiranui-isuzu.unity-mcp/CHANGELOG.md)

## 📄 ライセンス

MIT License — 詳細はリポジトリのライセンスファイルを参照。

---

Shiranui-Isuzu いすず
