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
jobs:
  restore:
    image: mcr.microsoft.com/dotnet/sdk:8.0
    commands:
      - dotnet restore

  test:
    image: mcr.microsoft.com/dotnet/sdk:8.0
    needs: [restore]        # restore の成功後に実行される
    commands:
      - dotnet test
```

| キー | 必須 | 説明 |
| :--- | :--- | :--- |
| `name` | 任意 | パイプライン名（表示用） |
| `jobs.<jobId>.image` | 必須 | ジョブを実行するコンテナイメージ |
| `jobs.<jobId>.commands` | 必須 | コンテナ内で `&&` 連結して実行されるコマンド列 |
| `jobs.<jobId>.needs` | 任意 | 依存する先行ジョブの ID 一覧 |

リポジトリはコンテナ内の `/workspace` にマウントされ、そこが作業ディレクトリになります。

## 実行履歴とログ

- ログ: `<リポジトリ>/.sebastian-ci/builds/<コミットハッシュ>/<ジョブID>.log`
- 成功マーカー: `<リポジトリ>/.sebastian-ci/builds/<コミットハッシュ>/success.marker`
  （存在する場合、同一コミットの再実行はデフォルトでスキップされます）

## アーキテクチャ

| クラス | 責務 |
| :--- | :--- |
| `Program` | CLI 引数の解析と全体制御 |
| `GitManager` | git コマンド制御（コミットハッシュ取得・変更検知） |
| `PipelineParser` | `.sebastian-ci.yaml` の読み込みとバリデーション |
| `DagEngine` | トポロジカルソートと並列実行制御 |
| `PodmanRunner` | `podman run` の非同期実行とログのストリーミング回収 |
| `BuildHistoryManager` | コミット単位のビルド履歴（スキップ判定・ログ置き場） |
| `ConsoleLogger` | スレッドセーフな色付きコンソール出力 |
