# sebastian-ci — 次世代ローカル完結型CIエンジン

Jenkins の弱点（重いサーバー常駐・プラグイン地獄・環境汚染）を克服するための、
C# (.NET 8) 製のローカル完結型 CI エンジンです。コンテナ実行には Podman を使用します。

## 特徴

- **サーバーレス**: 常駐プロセス不要。CLI を叩いた時だけ動く
- **Git ネイティブ**: 現在のコミットハッシュを自動取得し、実行済みコミットはスキップ（`--rebuild` で強制再実行）
- **クリーンな実行環境**: 各ジョブは `podman run --rm` の使い捨てコンテナ内で実行
- **DAG 並列実行**: `needs` による依存関係をトポロジカルソートで解決し、依存のないジョブは `Task.WhenAll` で並列実行
- **リアルタイムログ**: コンテナの標準出力・標準エラーをストリームで回収し、色付きコンソール表示とログファイル保存を同時に行う

## 必要環境

- .NET 8 SDK
- Git
- Podman

## 使い方

```bash
# ビルド
dotnet build src/SebastianCi/SebastianCi.csproj --configuration Release

# 実行（対象リポジトリ直下の .sebastian-ci.yaml を読み込む）
sebastian-ci /path/to/your/repo

# 実行済みコミットでも強制的に再実行
sebastian-ci /path/to/your/repo --rebuild

# 設定ファイル名を変更
sebastian-ci /path/to/your/repo --config my-pipeline.yaml
```

終了コード: 全ジョブ成功（またはスキップ判定）で `0`、それ以外は `1`。

## パイプライン定義 (`.sebastian-ci.yaml`)

```yaml
name: sample-pipeline
image: mcr.microsoft.com/dotnet/sdk:8.0   # グローバル既定イメージ
env:                                      # 全ジョブに -e で渡される環境変数
  DOTNET_NOLOGO: "1"
stages: [prepare, build]                  # 定義順 = 実行順（オプション）

jobs:
  restore:
    stage: prepare
    script:
      - dotnet restore

  lint:
    stage: prepare              # 同一ステージ内は並列実行される
    image: alpine:3.20          # ジョブ固有イメージ（グローバルを上書き）
    script:
      - echo lint

  test:
    stage: build
    needs: [restore]            # restore の成功後に実行される
    env:
      CONFIGURATION: Release    # 同名キーはジョブ側が優先
    script:
      - dotnet test
```

### スキーマ

| キー | 必須 | 説明 |
| :--- | :--- | :--- |
| `name` | 任意 | パイプライン名（表示用） |
| `image` | 条件付き | グローバル既定のコンテナイメージ。全ジョブが個別指定するなら省略可 |
| `env` | 任意 | 全ジョブへ `-e` で渡す環境変数マップ |
| `stages` | 任意 | ステージ名の配列。前ステージの全ジョブ完了後に次ステージが始まる |
| `jobs.<jobId>.image` | 条件付き | ジョブ固有イメージ。省略時はグローバル `image` を継承（両方空はエラー） |
| `jobs.<jobId>.stage` | 条件付き | 所属ステージ。`stages` 定義時は必須、未定義時は指定禁止 |
| `jobs.<jobId>.needs` | 任意 | 依存する先行ジョブの ID 一覧（DAG解析用） |
| `jobs.<jobId>.script` | 必須 | コンテナ内で `&&` 連結して実行されるコマンド列 |
| `jobs.<jobId>.env` | 任意 | ジョブ固有の環境変数。グローバル `env` とマージされ同名キーはジョブ側優先 |

スキーマに存在しないキー（例: 旧 `commands`）は typo 事故を防ぐためエラーになります。
リポジトリはコンテナ内の `/workspace` にマウントされ、そこが作業ディレクトリになります。

### バリデーション

以下はすべて `InvalidPipelineException` として実行前に検出されます。

- `needs` の循環参照（Kahn法トポロジカルソートで検知。自己依存・重複も不可）
- `needs` に存在しないジョブ名を指定
- `image`（グローバル・ジョブ両方が空）や `script` の欠落、空コマンド
- `stages` の重複・空名、未定義ステージの指定、後ステージのジョブへの `needs`

## 実行履歴とログ

- ログ: `<リポジトリ>/.sebastian-ci/builds/<コミットハッシュ>/<ジョブID>.log`
- 成功マーカー: `<リポジトリ>/.sebastian-ci/builds/<コミットハッシュ>/success.marker`
  （存在する場合、同一コミットの再実行はデフォルトでスキップされます）

## アーキテクチャ

| クラス | 責務 |
| :--- | :--- |
| `Program` | CLI 引数の解析と全体制御 |
| `GitManager` | git コマンド制御（コミットハッシュ取得・変更検知） |
| `PipelineParser` | `.sebastian-ci.yaml` の読み込み・バリデーション・正規化（イメージ継承と env マージ） |
| `DependencyGraph` | 実効依存関係（needs＋ステージ）の構築とトポロジカルソート |
| `DagEngine` | 実効依存関係に基づく並列実行制御 |
| `PodmanRunner` | `podman run` の非同期実行とログのストリーミング回収 |
| `BuildHistoryManager` | コミット単位のビルド履歴（スキップ判定・ログ置き場） |
| `ConsoleLogger` | スレッドセーフな色付きコンソール出力 |
