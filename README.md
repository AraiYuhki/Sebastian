# sebastian-ci — 次世代ローカル完結型CIエンジン

Jenkins の弱点（重いサーバー常駐・プラグイン地獄・環境汚染）を克服するための、
C# (.NET 8) 製のローカル完結型 CI エンジンです。コンテナ実行には Podman または Docker を使用します。

## 特徴

- **サーバーレス**: 常駐プロセス不要。CLI を叩いた時だけ動く
- **Git ネイティブ**: 現在のコミットハッシュを自動取得し、実行済みコミットはスキップ（`--rebuild` で強制再実行）
- **クリーンな実行環境**: 各ジョブは `podman run --rm` の使い捨てコンテナ内で実行
- **DAG 並列実行**: `needs` による依存関係をトポロジカルソートで解決し、依存のないジョブは `Task.WhenAll` で並列実行
- **リアルタイムログ**: コンテナの標準出力・標準エラーをストリームで回収し、色付きコンソール表示とログファイル保存を同時に行う

## 必要環境

- .NET 8 SDK
- Git
- Podman または Docker（既定では podman → docker の順で自動検出。`--engine` で明示指定可）

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

# コンテナエンジンを明示指定（既定は podman → docker の順で自動検出）
sebastian-ci /path/to/your/repo --engine docker

# 特定のジョブとその依存だけを狙い撃ちで実行（pre-commit フック等に）
sebastian-ci /path/to/your/repo --job test --job lint
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
| `jobs.<jobId>.artifacts` | 任意 | ジョブ成功後に退避する成果物パス（ワークスペース相対。絶対パス・`..` は不可） |
| `jobs.<jobId>.matrix` | 任意 | 変数名→値リストのマップ。全組み合わせにジョブが展開される（上限50通り） |
| `jobs.<jobId>.changes` | 任意 | グロブパターン配列。前回成功コミットとの差分が一致しない場合ジョブをスキップ |

スキーマに存在しないキー（例: 旧 `commands`）は typo 事故を防ぐためエラーになります。

### 環境変数の動的インジェクション

`env` の値では `$NAME` / `${NAME}` で**実行マシンの環境変数**を参照できます。
シークレットを YAML に書かず、ホストから引き継ぐための機能です。

```yaml
env:
  NUGET_API_KEY: $NUGET_API_KEY          # ホストの値をそのまま引き継ぐ
  ENDPOINT: https://${DEPLOY_HOST}/api   # 文字列への埋め込みも可能
  PRICE_LABEL: costs $$5                 # $$ はリテラルの $ にエスケープ
```

参照先のホスト環境変数が未定義の場合は、実行前に `InvalidPipelineException` で中断します
（空文字で黙って進んで本番を壊すより、早期に失敗させる設計です）。

### マトリックスビルド

`matrix` に変数名→値リストを書くと、**全組み合わせ分のジョブに展開されて並行実行**されます。
特別なキー `image` はコンテナイメージを切り替え、それ以外のキーは環境変数としてジョブに注入されます。

```yaml
jobs:
  test:
    matrix:
      image:
        - mcr.microsoft.com/dotnet/sdk:8.0
        - mcr.microsoft.com/dotnet/sdk:9.0
      configuration: [Debug, Release]
    script:
      - dotnet test --configuration "$configuration"
```

上記は `test[image=…8.0, configuration=Debug]` など **4ジョブ**に展開されます。
他のジョブが `needs: [test]` と書いた場合、依存は全バリアントに自動で書き換えられます
（全組み合わせの完了を待ってから実行）。組み合わせ数の上限は50です。

### 変更検知（Change Detection）によるジョブスキップ

`changes` にグロブパターンを書くと、**前回成功したビルドのコミットとの差分**
（`git diff --name-only`）がパターンに一致しない場合、そのジョブは「変更なし」としてスキップされます。

```yaml
jobs:
  test:
    changes: ["src/**", "tests/**", "*.csproj"]   # docs/ しか変わっていなければスキップ
    script: [dotnet test]
```

- 基準は history.json 上の直近の成功コミット。初回実行や基準が見つからない場合は安全側に倒して全ジョブを実行します
- 変更なしスキップ（`⏭ 変更なし`）は失敗ではないため、後続ジョブの実行を妨げず、パイプライン全体も成功として記録されます
- `.sebastian-ci/` 配下の差分は判定から自動で除外されます

### コンテナ実行とワークスペースのマウント

各ジョブは以下の形で使い捨てコンテナとして実行されます（Podman / Docker 共通）。

```
<podman|docker> run --rm \
  -e KEY=VALUE ... \
  --volume <リポジトリの絶対パス>:/workspace[:Z] \
  --workdir /workspace \
  <イメージ> /bin/sh -c "<script を && 連結したもの>"
```

- **自動マウント**: 対象リポジトリを絶対パスに解決し、存在検証したうえで `/workspace` にバインドマウントします
- **作業ディレクトリ**: `--workdir /workspace` を指定するため、`script` はリポジトリ直下で実行されます
- **自動クリーンアップ**: `--rm` 付きで起動するため、ジョブ終了時にコンテナは自動破棄され、ゴミが残りません
- **SELinux対応**: Podman 使用時はマウントに `:Z` を付与し、SELinux 環境でも安全にアクセスできます（Docker では付与しません）
- **インジェクション対策**: 引数は `ProcessStartInfo.ArgumentList` で個別に渡すため、シェル経由の引数解釈は発生しません

### 中断・強制終了時のクリーンアップ（ゾンビコンテナ防止）

各コンテナは `--name sebastian-ci-<ジョブID>-<乱数8桁>` の一意な名前で起動され、
実行中は中央のレジストリで追跡されます。

**Ctrl+C（SIGINT）** は `Console.CancelKeyPress` でハンドリングされます:

1. 即時終了を抑止して協調的キャンセルに切り替え、実行中の全ジョブへキャンセルを伝播
2. 追跡中のすべてのコンテナへ `stop -t 2 <コンテナ名>` → `rm <コンテナ名>` を並列（`Task.WhenAll`）で発行
3. クリーンアップ完了後、終了コード `130`（128 + SIGINT）で終了

**SIGTERM（kill）や未処理例外**による終了は `AppDomain.CurrentDomain.ProcessExit` が
最終防衛線としてハンドリングし、追跡中のコンテナが残っていれば同じ停止・削除処理を
実行してから終了します（SIGTERM 時の終了コードは `143`）。

完走済みのジョブのコンテナは追跡から外れているため、停止対象にはなりません。
どの経路でもクリーンアップは一度しか実行されません。

### 特定ジョブの狙い撃ち実行（--job）

`--job <ジョブID>` を指定すると、**そのジョブと依存ジョブ（needs の推移的閉包）だけ**を
実行します。pre-commit フックなど「全体は重いが一部だけ回したい」場面向けの機能です。

```bash
# test とその依存（restore → build）だけを実行。無関係な lint / deploy は走らない
sebastian-ci . --job test

# 複数指定も可能
sebastian-ci . --job lint --job build
```

- マトリックスジョブは元のID（例: `test`）で指定すると全バリアントが対象になります
- `--job` 指定時は「実行済みコミットのスキップ判定」を通らず常に実行されます
- 部分実行は全体の成功を意味しないため、**実行履歴（history.json）には記録されません**
- 存在しないジョブIDを指定した場合は実行前にエラーで中断します

### バリデーション

以下はすべて `InvalidPipelineException` として実行前に検出されます。

- `needs` の循環参照（Kahn法トポロジカルソートで検知。自己依存・重複も不可）
- `needs` に存在しないジョブ名を指定
- `image`（グローバル・ジョブ両方が空）や `script` の欠落、空コマンド
- `stages` の重複・空名、未定義ステージの指定、後ステージのジョブへの `needs`

## 実行履歴・ログ・成果物の永続化

管理ディレクトリ（既定: `<リポジトリ>/.sebastian-ci/`、`--data-dir` で変更可）は自動作成され、
以下のレイアウトで永続化されます。

```
.sebastian-ci/
├── history.json                          # コミットハッシュをキーとした実行メタデータ
├── logs/<コミットハッシュ>/<ジョブID>.log   # ジョブごとの標準出力・標準エラー（リアルタイム保存）
└── artifacts/<コミットハッシュ>/<ジョブID>/  # artifacts で宣言した成果物の退避先
```

`history.json` には実行ごとに「実行日時・成否・ログディレクトリへのパス・ジョブ別結果」が
保存・更新されます。成功記録のあるコミットは次回以降スキップされます（`--rebuild` で強制再実行）。
失敗した実行も記録されますが、スキップ対象にはなりません。ファイルが破損していた場合は
警告を出して履歴を初期化し、実行は継続します。

```json
{
  "f6184070…": {
    "executedAt": "2026-07-11T10:37:07+09:00",
    "isSuccess": true,
    "logDirectoryPath": "/path/to/repo/.sebastian-ci/logs/f6184070…",
    "jobs": [ { "jobId": "build", "status": "Success", "durationSeconds": 1.0 } ]
  }
}
```

## アーキテクチャ

| クラス | 責務 |
| :--- | :--- |
| `Program` | CLI 引数の解析と全体制御 |
| `GitManager` | git コマンド制御（コミットハッシュ取得・変更検知） |
| `PipelineParser` | `.sebastian-ci.yaml` の読み込み・バリデーション・正規化（イメージ継承と env マージ） |
| `EnvironmentVariableExpander` | env 値の `$NAME` / `${NAME}` をホスト環境変数で展開 |
| `MatrixExpander` | matrix ジョブの全組み合わせ展開と needs の書き換え |
| `ChangeDetector` | 前回成功コミットとの差分とグロブパターンによる実行要否判定 |
| `JobSelector` | `--job` 指定ジョブ＋依存（推移的閉包）への絞り込み |
| `DependencyGraph` | 実効依存関係（needs＋ステージ）の構築とトポロジカルソート |
| `DagEngine` | 実効依存関係に基づく並列実行制御 |
| `ContainerEngine` | Podman / Docker の差分吸収（マウントオプション等）と自動検出 |
| `ContainerRunner` | `podman run` / `docker run` の非同期実行とログのストリーミング回収 |
| `ActiveContainerRegistry` | 実行中コンテナのスレッドセーフな中央追跡 |
| `ContainerCleanup` | 中断時の全アクティブコンテナの停止（`stop -t 2`）と削除（`rm`） |
| `HistoryManager` | history.json への実行メタデータの保存・更新とスキップ判定、ログ置き場の用意 |
| `ArtifactManager` | 成果物の `artifacts/<コミットハッシュ>/<ジョブID>/` への退避 |
| `ConsoleLogger` | スレッドセーフな色付きコンソール出力 |
