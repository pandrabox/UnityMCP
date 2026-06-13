# インストール手順

## manifest.json による追記インストール

既存の Unity プロジェクトに本パッケージをインストールします。

### ステップ 1: manifest.json を開く

Unity プロジェクトフォルダの以下のファイルをテキストエディタで開きます：

```
YourUnityProject/
└── Packages/
    └── manifest.json
```

### ステップ 2: 依存パッケージを追加

`manifest.json` の `"dependencies"` セクションに以下を追加します：

```json
"jp.shiranui-isuzu.unity-mcp": "git+https://github.com/pandrabox/UnityMCP.git#main"
```

**修正前：**
```json
{
  "dependencies": {
    "com.unity.ide.vscode": "1.2.5"
  }
}
```

**修正後：**
```json
{
  "dependencies": {
    "com.unity.ide.vscode": "1.2.5",
    "jp.shiranui-isuzu.unity-mcp": "git+https://github.com/pandrabox/UnityMCP.git#main"
  }
}
```

> ⚠️ JSON の最後のカンマに注意してください。最後の項目の後にはカンマを付けません。

### ステップ 3: Unity Editor を開く

ファイルを保存して Unity Editor を開くと、自動的にパッケージがダウンロード・インストールされます。

### ステップ 4: 動作確認

Unity Editor のログで以下のような出力が見られたら成功です：

```
Unity MCP パッケージが読み込まれました
```

---

## バージョンを指定する場合

特定のバージョンを指定したい場合は、タグ名で指定できます：

```json
"jp.shiranui-isuzu.unity-mcp": "git+https://github.com/pandrabox/UnityMCP.git#v2.1.0"
```

## トラブルシューティング

**JSON フォーマットエラーが出た場合：**
- JSON の構文を確認してください
- オンラインの JSON Validator を使用して確認できます
- 特にカンマの位置と括弧のバランスを確認してください

**パッケージがインストールされない場合：**
- manifest.json を保存してから Unity Editor を開き直してください
- Console ウィンドウでエラーを確認してください
