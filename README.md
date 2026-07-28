# sebastian-ci — ローカルで完結する軽量CIエンジン

![CI](https://github.com/AraiYuhki/Sebastian/actions/workflows/ci.yml/badge.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-Apache--2.0-blue)

**sebastian-ci** は、CI（継続的インテグレーション）をあなたのマシン上だけで実行するための、C#（.NET 8）製コマンドラインツールです。
テストやビルドの手順を1つのYAMLファイルに書いておくと、コマンド1つでコンテナの中で順番に（依存関係のないものは並列に）実行してくれます。

> 📖 関連ドキュメント： [コントリビューションガイド](CONTRIBUTING.md) ・ [変更履歴](CHANGELOG.md) ・ [セキュリティ](SECURITY.md) ・ [サンプル定義](examples/) ・ [製品紹介ページ](docs/sebastian-ci-overview.html)

Jenkins のような従来のCIツールが抱える悩み——「サーバーを常時立てておく必要がある」「プラグインの管理が煩雑」「実行するたびにマシンの環境が汚れる」——を、次の3点で解決します。

- **サーバー不要**：常駐プロセスは一切なし。コマンドを叩いたときだけ動きます。
- **環境を汚さない**：各ステップは使い捨てのコンテナ（Podman / Docker）の中で動くので、ホストマシンにツールをインストールする必要がありません。
- **Gitと連動**：同じコミットを2回実行すると、2回目は自動でスキップ。必要なときだけ動きます。

git のコミット前チェック（pre-commit フック）に組み込んだり、手元で「push する前に一通り確認したい」といった用途に向いています。

---

## 目次

- [動作に必要なもの](#動作に必要なもの)
- [クイックスタート](#クイックスタート)
- [基本的な使い方](#基本的な使い方)
- [パイプライン定義ファイルを書く](#パイプライン定義ファイルを書く)
  - [最小の例](#最小の例)
  - [実践的な例](#実践的な例)
  - [設定できる項目の一覧](#設定できる項目の一覧)
- [主な機能](#主な機能)
  - [ジョブの依存関係と並列実行](#1-ジョブの依存関係と並列実行needs)
  - [ステージ](#2-ステージstages)
  - [環境変数の受け渡し](#3-環境変数の受け渡しenv)
  - [成果物の保存](#4-成果物の保存artifacts)
  - [マトリックスビルド](#5-マトリックスビルドmatrix)
  - [変更検知によるスキップ](#6-変更検知によるスキップchanges)
  - [失敗への対処](#7-失敗への対処retry--continueonerror)
  - [ジョブのタイムアウト](#8-ジョブのタイムアウトtimeout)
  - [同時実行数の制限](#9-同時実行数の制限--max-parallel)
  - [キャッシュによる高速化](#10-キャッシュによる高速化cache)
  - [構成の検証だけ行う](#11-構成の検証だけ行う--validate)
  - [特定ジョブだけを実行する](#12-特定ジョブだけを実行する--job)
  - [マシンのリソース監視](#13-マシンのリソース監視resources)
  - [Slack / ChatWork への通知](#14-slack--chatwork-への通知notifications)
  - [コンテナを使わないホスト直接実行](#15-コンテナを使わないホスト直接実行shell)
  - [実行時パラメーター](#16-実行時パラメーターparams)
  - [手動承認ゲート](#17-手動承認ゲートapproval)
  - [成否に関わらず実行する後処理](#18-成否に関わらず実行する後処理afterscript)
- [ゲームエンジンでの利用例（Godot / Unity / Unreal）](#ゲームエンジンでの利用例godot--unity--unreal)
- [実行結果・ログ・成果物の保存場所](#実行結果ログ成果物の保存場所)
- [中断してもコンテナが残らない仕組み](#中断してもコンテナが残らない仕組み)
- [コマンドラインオプション一覧](#コマンドラインオプション一覧)
- [よくある質問（FAQ）](#よくある質問faq)
- [アーキテクチャ](#アーキテクチャ)

---

## 動作に必要なもの

| 必要なもの | 用途 |
| :--- | :--- |
| [.NET 8 SDK](https://dotnet.microsoft.com/) | 本ツールのビルドと実行 |
| Git | コミットハッシュの取得と変更検知 |
| [Podman](https://podman.io/) または [Docker](https://www.docker.com/) | 各ジョブを実行するコンテナ環境（`podman` → `docker` の順に自動検出） |

> **メモ**：Podman と Docker のどちらでも動きます。両方インストールされている場合は Podman が優先されます。`--engine docker` のように明示的に選ぶこともできます。

---

## クイックスタート

### 1. ビルドする

```bash
dotnet build src/SebastianCi/SebastianCi.csproj --configuration Release
```

### 2. パイプライン定義ファイルを用意する

チェックしたいリポジトリの直下に `.sebastian-ci.yaml` というファイルを作ります。

```yaml
name: my-first-pipeline
image: alpine:3.20          # 各ジョブを動かすコンテナイメージ

jobs:
  hello:
    script:
      - echo "Hello, sebastian-ci!"
```

### 3. 実行する

```bash
sebastian-ci /path/to/your/repo
```

コンテナの中で `echo` が実行され、出力がリアルタイムに画面へ表示されれば成功です。同じコミットのまま再度実行すると、今度は「実行済みのためスキップ」と表示されます。

---

## 基本的な使い方

```bash
# 対象リポジトリ直下の .sebastian-ci.yaml を読み込んで実行
sebastian-ci /path/to/your/repo

# パスを省略するとカレントディレクトリが対象になる
sebastian-ci
```

**終了コード**は、CIツールらしくシェルスクリプトやフックから判定しやすいようになっています。

| 終了コード | 意味 |
| :--- | :--- |
| `0` | 全ジョブが成功（または「変更なし」でスキップ） |
| `1` | いずれかのジョブが失敗、または設定ファイルが不正 |
| `130` | 実行中に `Ctrl+C` で中断された |
| `143` | 実行中に `kill`（SIGTERM）で終了された |

---

## パイプライン定義ファイルを書く

パイプライン（＝一連の処理手順）は、リポジトリ直下の `.sebastian-ci.yaml` に記述します。

### 最小の例

```yaml
image: alpine:3.20     # すべてのジョブ共通で使うコンテナイメージ

jobs:
  greet:
    script:
      - echo "ビルドを開始します"
```

- `jobs` の下に、ジョブを好きな名前（この例では `greet`）で並べていきます。
- 各ジョブには最低限、コンテナ内で実行するコマンドの一覧（`script`）が必要です。
- コンテナイメージは、全体共通の `image` か、ジョブごとの `image` のどちらかで必ず指定します。

### 実践的な例

```yaml
name: sample-pipeline               # パイプラインの名前（表示用）
image: mcr.microsoft.com/dotnet/sdk:8.0   # 全ジョブ共通の既定イメージ
env:                                # 全ジョブに渡す環境変数
  DOTNET_NOLOGO: "1"
stages: [prepare, build]            # ステージ（実行の大きな順序）

jobs:
  restore:
    stage: prepare
    script:
      - dotnet restore

  lint:
    stage: prepare                  # restore と同じステージなので並列実行される
    image: alpine:3.20              # このジョブだけ別のイメージを使う
    script:
      - echo "コード整形チェック"

  test:
    stage: build                    # prepare ステージが全部終わってから始まる
    needs: [restore]                # restore の成功を待つ
    env:
      CONFIGURATION: Release        # 全体の env に加えて、このジョブ専用の変数
    script:
      - dotnet test
```

### 設定できる項目の一覧

**パイプライン全体の設定**

| キー | 必須 | 説明 |
| :--- | :--- | :--- |
| `name` | 任意 | パイプラインの名前（画面表示に使われるだけ） |
| `image` | 条件付き | 全ジョブ共通の既定コンテナイメージ。すべてのジョブが個別に `image` を指定するなら省略可 |
| `env` | 任意 | 全ジョブに渡す環境変数（`キー: 値` の形式） |
| `stages` | 任意 | ステージ名の並び。書いた順が実行順になる |
| `jobs` | **必須** | ジョブ定義の集まり（1つ以上） |
| `params` | 任意 | 実行時パラメーターの定義（`--param` で値を渡す・後述） |
| `resources` | 任意 | リソース警告のしきい値（`minMemoryMb` / `minDiskMb`） |
| `notifications` | 任意 | Slack / ChatWork への通知設定（後述） |
| `plugins` | 任意 | 機能を拡張するプラグイン（NuGet パッケージ／ローカルDLL・後述） |

**ジョブごとの設定**（`jobs.<ジョブ名>` の下に書く）

| キー | 必須 | 説明 |
| :--- | :--- | :--- |
| `script` | **必須** | コンテナ内で実行するコマンドの一覧。上から順に `&&` でつないで実行される |
| `afterScript` | 任意 | `script` の成否に関わらず最後に実行される後処理コマンドの一覧（後述） |
| `image` | 条件付き | このジョブ専用のイメージ。省略すると全体の `image` を引き継ぐ（両方空だとエラー） |
| `stage` | 条件付き | 所属するステージ。`stages` を定義したときは必須、定義していないときは指定不可 |
| `needs` | 任意 | 先に成功していてほしいジョブの名前一覧（依存関係） |
| `env` | 任意 | このジョブ専用の環境変数。全体の `env` とマージされ、同名なら**ジョブ側が優先** |
| `artifacts` | 任意 | ジョブ成功後に保存したい成果物のパス一覧（リポジトリからの相対パス） |
| `matrix` | 任意 | 同じジョブを複数の条件で並列実行するための設定（後述） |
| `changes` | 任意 | このパターンに合う変更がなければジョブをスキップする（後述） |
| `timeout` | 任意 | ジョブの制限時間（秒）。超過するとコンテナを停止して失敗扱いにする（`0`／省略で無制限） |
| `retry` | 任意 | 失敗時の再試行回数（`0`／省略で再試行なし） |
| `continueOnError` | 任意 | `true` なら失敗しても後続ジョブと全体成否に影響させない |
| `cache` | 任意 | コミットをまたいで永続化するコンテナ内パス一覧（絶対パス）。実行の高速化に使う |
| `agent` | 任意 | このジョブを実行するリモートエージェントのURL（未指定ならローカル実行） |
| `agentToken` | 任意 | リモートエージェントの認証トークン（`$NAME` でホスト環境変数を参照可） |
| `remote` | 任意 | `true` なら `agents` プールの中で最も空いているエージェントで実行する |
| `runner` | 任意 | プラグインが提供する独自ジョブランナーの名前（`agent` / `remote` と併用不可） |
| `shell` | 任意 | `true` ならコンテナを使わず、ホストマシン上で `script` を直接実行する（`image` / `cache` と併用不可・後述） |
| `approval` | 任意 | 実行前の手動承認ゲートのメッセージ。指定するとコンソールで `y` の入力を待つ（後述） |

> **ヒント**：定義ファイルに知らないキー（たとえば古い書き方の `commands`）を書くとエラーになります。タイプミスに気づけるように、あえて「知らないキーは受け付けない」仕様になっています。

---

## 主な機能

### 1. ジョブの依存関係と並列実行（`needs`）

ジョブ同士の「先にこれを終わらせてほしい」という関係を `needs` で指定します。
sebastian-ci はこの依存関係を **DAG（有向非巡回グラフ：ループのない依存関係の図）** として解析し、
**依存関係のないジョブは自動的に並列実行**します。

```yaml
jobs:
  restore:
    script: [dotnet restore]
  build:
    needs: [restore]        # restore の後
    script: [dotnet build]
  lint:
    script: [echo lint]     # 何にも依存しないので restore と同時に走る
```

- 依存先が**失敗**すると、それに続くジョブは実行されず「スキップ」になります。
- 依存関係がループしている（AがBを待ち、BがAを待つ）場合は、実行前にエラーで止まります。

### 2. ステージ（`stages`）

`stages` を使うと、より大きな単位で実行の順序を区切れます。
**あるステージの全ジョブが終わるまで、次のステージは始まりません。**
同じステージ内のジョブは（`needs` がなければ）並列に走ります。

```yaml
stages: [prepare, build, deploy]   # この順で進む

jobs:
  install:  { stage: prepare, script: [echo install] }
  compile:  { stage: build,   script: [echo compile] }
  release:  { stage: deploy,  script: [echo release] }
```

### 3. 環境変数の受け渡し（`env`）

`env` に書いた値は、コンテナに環境変数として渡されます。
さらに、`$NAME` や `${NAME}` と書くと、**実行しているマシン（ホスト）の環境変数の値を引き継げます。**
APIキーなどの秘密情報をYAMLに直接書かずに済むのが利点です。

```yaml
env:
  NUGET_API_KEY: $NUGET_API_KEY          # ホストの環境変数の値をそのまま渡す
  ENDPOINT: https://${DEPLOY_HOST}/api   # 文字列の一部に埋め込むこともできる
  PRICE_LABEL: costs $$5                 # $$ と書くと、ただの $ になる（エスケープ）
```

> 参照したホスト側の環境変数が定義されていない場合は、**空文字で進めず、実行前にエラーで止まります。** 秘密情報が空のまま本番に進んで事故になるのを防ぐためです。

**組み込み環境変数**：すべてのジョブに、実行コンテキストを表す次の環境変数が自動で渡されます
（同名のキーを `env` で定義した場合はそちらが優先されます）。

| 変数 | 値 |
| :--- | :--- |
| `CI` / `SEBASTIAN_CI` | 常に `true`（CI環境であることの一般的な合図） |
| `SEBASTIAN_CI_PIPELINE` | パイプライン名（`name`） |
| `SEBASTIAN_CI_JOB` | 実行中のジョブID（matrix 展開後の名前） |
| `SEBASTIAN_CI_COMMIT` | 対象コミットのハッシュ（40桁） |
| `SEBASTIAN_CI_COMMIT_SHORT` | 対象コミットの短縮ハッシュ（8桁） |
| `SEBASTIAN_CI_BRANCH` | 現在のブランチ名（切り離しHEADでは `HEAD`） |

**シークレットの自動マスキング**：`$NAME` / `${NAME}` でホストから受け取った値は秘密情報として扱われ、
ジョブの出力（コンソール・ログファイル・ダッシュボードのライブログ）に現れると自動で `****` に伏せ字化されます。
`echo $NUGET_API_KEY` のようなうっかり出力や、ツールがエラーメッセージにトークンを含めてしまうケースでも、
ログに平文が残りません（誤検知を避けるため、4文字未満の短い値はマスクされません）。

### 4. 成果物の保存（`artifacts`）

ジョブが生成したファイル（ビルド結果など）を、実行後に手元へ残しておけます。
`artifacts` に保存したいパスを書くと、ジョブ成功後に自動でコピーされます。

```yaml
jobs:
  build:
    script:
      - dotnet publish -o publish-output
    artifacts:
      - publish-output      # このフォルダを丸ごと保存
```

保存先は `.sebastian-ci/artifacts/<コミットハッシュ>/<ジョブ名>/` です（詳細は[後述](#実行結果ログ成果物の保存場所)）。
なお、リポジトリの外を指すパス（絶対パスや `..` を含むもの）は安全のため受け付けません。

### 5. マトリックスビルド（`matrix`）

「.NET 8 と .NET 9 の両方でテストしたい」「Debug と Release の両方をビルドしたい」——
こうした**同じジョブの複数条件での実行**を、`matrix` 一箇所にまとめて書けます。
指定した条件の**全組み合わせ**にジョブが展開され、並列に実行されます。

```yaml
jobs:
  test:
    matrix:
      image:                                  # 特別なキー。コンテナイメージを切り替える
        - mcr.microsoft.com/dotnet/sdk:8.0
        - mcr.microsoft.com/dotnet/sdk:9.0
      configuration: [Debug, Release]         # 通常のキー。環境変数として渡される
    script:
      - dotnet test --configuration "$configuration"
```

- 上の例は `image` 2種 × `configuration` 2種 ＝ **4ジョブ**に展開されます。
  （`test[image=…8.0, configuration=Debug]` のような名前が付きます）
- `image` は特別扱いで、コンテナイメージそのものを切り替えます。それ以外のキーは環境変数としてジョブに渡ります。
- 他のジョブが `needs: [test]` と書いていれば、依存は**展開後の全ジョブ**に自動で張り直されます（全部の完了を待ちます）。
- 組み合わせ数の上限は 50 です。

### 6. 変更検知によるスキップ（`changes`）

「コミットは進んだが、`docs/` の下しか変わっていない。それならテストは省略したい」——
そんな最適化を `changes` で実現できます。
**前回このパイプラインが成功したコミット**と現在のコミットの差分を調べ、
`changes` に書いたパターンに一致するファイルが1つも変わっていなければ、そのジョブをスキップします。

```yaml
jobs:
  test:
    changes: ["src/**", "tests/**", "*.csproj"]   # ソースが変わったときだけ実行
    script: [dotnet test]
  docs:
    changes: ["docs/**"]                          # ドキュメントが変わったときだけ実行
    script: [echo "ドキュメントをビルド"]
```

- 差分の基準は、履歴（`history.json`）に残っている直近の成功コミットです。
- 初回実行など基準が見つからないときは、安全のため**すべてのジョブを実行**します。
- この「変更なしスキップ」は**失敗ではありません**。後続のジョブは普通に実行され、パイプライン全体も成功扱いになります。
- sebastian-ci 自身の管理フォルダ（`.sebastian-ci/`）の変更は、判定から自動で除外されます。

### 7. 失敗への対処（`retry` / `continueOnError`）

不安定なテストや、失敗しても止めたくない補助的なジョブに対応するための2つの設定です。

```yaml
jobs:
  test:
    retry: 2                # 失敗したら最大2回まで再実行する
    script: [dotnet test]
  optional-lint:
    continueOnError: true   # 失敗してもパイプラインを止めない
    script: [run-experimental-linter]
```

- **`retry`**：ジョブが失敗したとき、指定回数まで自動で再実行します（`0`／省略で再試行なし）。ネットワーク起因などの一時的な失敗に有効です。
- **`continueOnError`**：`true` にすると、そのジョブが失敗しても**後続ジョブは実行され、パイプライン全体も成功扱い**になります。結果には `⚠ 失敗(許容)` と表示されます。

### 8. ジョブのタイムアウト（`timeout`）

コマンドがハングしても永遠に待ち続けないよう、ジョブごとに制限時間（秒）を設定できます。
超過するとそのコンテナを停止し、ジョブを**失敗扱い**にして次に進みます。

```yaml
jobs:
  test:
    timeout: 300          # 5分を超えたら打ち切る
    script: [dotnet test]
```

`0` または省略で無制限です。無人でCIを回すときの保険になります。

### 9. 同時実行数の制限（`--max-parallel`）

依存関係のないジョブは既定ですべて同時に走ります。matrixで大量に展開されるとマシンが逼迫するため、
`--max-parallel` で同時に動くコンテナ数の上限を設けられます。

```bash
# 同時に走るコンテナを最大2個までに制限する
sebastian-ci . --max-parallel 2
```

依存待ちや「変更なしスキップ」は枠を消費せず、**実際にコンテナを起動するジョブだけ**が枠を取ります。

### 10. キャッシュによる高速化（`cache`）

各ジョブは毎回まっさらな使い捨てコンテナで動くため、そのままでは `dotnet restore` や
`npm install` のダウンロードが毎回発生します。`cache` に「コミットをまたいで残したい
コンテナ内のパス」を書くと、その場所を**実行間で永続化**し、次回以降を高速化できます。

```yaml
jobs:
  restore:
    cache:
      - /root/.nuget/packages     # NuGet のパッケージキャッシュを残す
    script: [dotnet restore]
```

- 各キャッシュパスは `.sebastian-ci/cache/<パス名>/` に保存され、コンテナ内の同じ場所にマウントされます。
- 保存先は**コミットハッシュに依存しません**。だからコミットが進んでも前回のキャッシュがそのまま効きます。
- パスは**コンテナ内の絶対パス**で指定します（相対パスはエラー）。
- matrixで展開された各バリアントも同じキャッシュを共有します。

### 11. 構成の検証だけ行う（`--validate`）

`--validate` を付けると、**ジョブを実行せずに構成ファイルの正しさだけ**を確認します。
コンテナもGitも履歴も一切触らないので高速で、ビルドが重いパイプライン（後述のゲームエンジン等）や
pre-commitフックでの事前チェックに向いています。

```bash
sebastian-ci . --validate
```

チェックされるのは、YAMLの構文、スキーマ（未知キー・必須項目・型）、`matrix` の展開、
`needs` の循環や未定義参照、そして `env` が参照する**ホスト環境変数が実際に存在するか**までです。
問題がなければ展開後のジョブ一覧を表示して終了コード `0`、問題があれば `1` を返します。

### 12. 特定ジョブだけを実行する（`--job`）

パイプライン全体を回すと重いとき、`--job` で**特定のジョブとその依存だけ**を実行できます。
pre-commit フックなど「一部だけサッと確認したい」場面に便利です。

```bash
# test と、その前提になる restore・build だけを実行（lint や deploy は走らない）
sebastian-ci . --job test

# 複数のジョブを指定することもできる
sebastian-ci . --job lint --job build
```

- 指定したジョブが依存しているジョブ（`needs` をたどった先すべて）も自動的に含まれます。
- マトリックスジョブは元の名前（例：`test`）を指定すると、展開後の全バリアントが対象になります。
- `--job` を使ったときは、実行済みコミットでも必ず実行されます（スキップ判定を通りません）。
- 一部だけの実行なので、この結果は履歴（`history.json`）には記録されません。「全部成功した」と誤って記録され、後の全体実行が飛ばされてしまうのを防ぐためです。

### 13. マシンのリソース監視（`resources`）

実行のたびに、動かしているマシンの**メモリとディスクの空き状況を表示**します。
さらに、空きが設定したしきい値を下回ると**警告**を出します。重いビルドの前に
「ディスクが足りない」といった事故に気づけます。

```yaml
resources:
  minMemoryMb: 512      # 空きメモリがこれ未満なら警告（0 で無効）
  minDiskMb: 1024       # 空きディスクがこれ未満なら警告（0 で無効）
```

実行時にはまず次のように現在の状況が表示されます。

```
🖥 リソース: メモリ 15043/16075 MB 空き, ディスク 29794/258019 MB 空き
```

`resources` を書かなくても表示は行われます（しきい値は既定でメモリ512MB・ディスク1024MB）。

### 14. Slack / ChatWork への通知（`notifications`）

パイプラインの節目に **Slack や ChatWork へメッセージを送れます**。
`on` で**送るタイミングを自分で選べる**のがポイントです。

```yaml
notifications:
  - type: slack
    on: [failure]                 # 失敗したときだけ通知
    webhook: $SLACK_WEBHOOK_URL    # 秘密情報はホスト環境変数から受け取る
  - type: chatwork
    on: [success, failure]        # 成功・失敗の両方（＝完了時）に通知
    token: $CHATWORK_TOKEN
    room: $CHATWORK_ROOM_ID
```

**送信タイミング（`on`）** に指定できるのは次の3つで、複数を並べられます。

| 値 | 送られるタイミング |
| :--- | :--- |
| `start` | パイプラインの開始時 |
| `success` | 成功して完了したとき |
| `failure` | 失敗して完了したとき |

**通知先ごとの設定**

| 種別 | 必須項目 | 説明 |
| :--- | :--- | :--- |
| `slack` | `webhook` | Slack の Incoming Webhook URL に JSON を POST する |
| `chatwork` | `token`, `room` | ChatWork API にルームIDとトークンでメッセージを送る |

- `webhook` / `token` / `room` は `$NAME` でホスト環境変数を参照できます。**秘密情報をYAMLに書かずに済みます**。
- 通知の送信に失敗しても、**パイプライン自体は止まりません**（警告を出して続行します）。
- `--validate` は、`on` の値・種別・必須項目に加えて、参照するホスト環境変数が存在するかまで確認します。

### 15. コンテナを使わないホスト直接実行（`shell`）

各ジョブは通常コンテナの中で動きますが、**Xcode（macOS / iOS ビルド）のようにコンテナでは動かせないツールチェーン**もあります。
`shell: true` を指定したジョブは、コンテナを使わず **sebastian-ci を実行しているマシン上で `script` を直接実行**します
（Jenkins がエージェント上でコマンドを直接叩くのと同じ感覚です）。

```yaml
jobs:
  export-ios:
    # Unity エディタでの Xcode プロジェクト書き出しまでは、これまでどおりコンテナで
    image: unityci/editor:ubuntu-2022.3.10f1-ios-3
    script:
      - unity-editor -quit -batchmode -nographics -projectPath . -buildTarget iOS
        -executeMethod BuildScript.PerformBuild

  package-ipa:
    needs: [export-ios]
    shell: true            # xcodebuild は macOS でしか動かないため、ホストで直接実行
    script:
      - xcodebuild -exportArchive -archivePath build/ios-archive/Unity-iPhone.xcarchive
        -exportPath build/ipa -exportOptionsPlist ExportOptions.plist
    artifacts:
      - build/ipa
```

- 作業ディレクトリは対象リポジトリの直下です。**ホストの環境変数（`PATH` など）を引き継ぎ**、その上にジョブの `env` が上書きされます。
- `image` は不要です（グローバル `image` も継承しません）。`cache` は併用できません（ホスト実行では環境がそのまま残るため不要です）。
- `timeout` / `retry` / `continueOnError` / `artifacts` / `matrix` / `changes` / `needs` は、コンテナジョブと同じように使えます。タイムアウト超過や中断（Ctrl+C）時は、**起動したプロセスツリーごと停止**します。
- `agent` / `remote` と併用すると、**エージェント側のホストで直接実行**されます（[分散実行](#分散実行slaveagent)を参照）。Mac をエージェントにすれば、マスターがどの OS でも Xcode の工程を組み込めます。
- パイプラインが shell ジョブ（とリモート / プラグイン実行）だけで構成されている場合、**podman / docker が無いマシンでも実行できます**。
- **注意**：コンテナと違って環境の隔離はありません。必要なツールは実行するマシンにあらかじめインストールしておいてください。再現性が重要なジョブには、これまでどおりコンテナ実行をおすすめします。

### 16. 実行時パラメーター（`params`）

「同じパイプラインを、実行のたびに違う条件で動かしたい」——デプロイ先の切り替えや
バージョン番号の指定のような**実行時の入力**を、`params` として定義できます
（Jenkins のパラメータ付きビルドに相当します）。値は環境変数として全ジョブに渡されます。

```yaml
params:
  deployTarget:
    default: staging                # 省略時に使われる値
    description: デプロイ先の環境
    choices: [staging, production]  # これ以外の値はエラーになる
  releaseVersion:
    description: リリースするバージョン   # default が無い ＝ --param での指定が必須

jobs:
  deploy:
    script:
      - ./deploy.sh --target "$deployTarget" --version "$releaseVersion"
```

```bash
# --param で値を渡して実行する
sebastian-ci . --param deployTarget=production --param releaseVersion=1.4.0
```

- **`default`**：`--param` を省略したときに使われる値です。`default` の無いパラメーターは指定が必須で、漏れると実行前にエラーで止まります。
- **`choices`**：許可する値を絞れます。範囲外の値（`default` 含む）は実行前にエラーになります。
- **優先順位**：同名の環境変数がある場合、`グローバル env < params < ジョブ env（matrix 含む）` の順で後勝ちです。
- **値はそのまま渡る**：`--param` で渡した値は `$NAME` 展開の対象になりません（`$` を含む値も安全に渡せます）。
- **履歴に残らない**：`--param` を指定した実行は「そのコミットの通常実行」とは別物のため、成功しても実行済みスキップの対象にはなりません（`--job` と同じ扱い）。
- `--validate` でも同じ検証が行われるため、`--validate --param name=value` で構成だけ確認できます。

### 17. 手動承認ゲート（`approval`）

「テストまでは自動で回すが、本番デプロイの直前だけは人間が確認したい」——
Jenkins の `input` ステップに相当する**承認ゲート**を、ジョブに `approval` を書くだけで作れます。

```yaml
jobs:
  test:
    script: [dotnet test]
  deploy:
    needs: [test]
    approval: 本番環境へデプロイします。よろしいですか？   # ここで実行が一時停止する
    script: [./deploy.sh production]
```

実行がこのジョブに到達すると、メッセージを表示して**コンソールで `y` の入力を待ちます**。

```
🔐 ジョブ 'deploy' は承認待ちです: 本番環境へデプロイします。よろしいですか？
   実行してよければ y を入力してください（それ以外は中止） >
```

- **承認**（`y` / `yes`）で実行が続きます。**それ以外の入力は拒否**となり、そのジョブは失敗扱い・後続ジョブはスキップされます。
- 承認の確認は、依存（`needs`）と変更検知（`changes`）を通過した**実行の直前**に行われます。並列実行中でも承認の問い合わせは1件ずつ順番に表示されます。
- **`--yes`** を付けると、すべての承認ゲートを自動承認します（無人実行・スクリプトからの起動向け）。
- 標準入力が使えない環境（ダッシュボードの実行ボタン・フックからの起動など）では承認を確認できないため拒否扱いになります。無人で通したい場合は `--yes` を使ってください。

### 18. 成否に関わらず実行する後処理（`afterScript`）

「テストが失敗しても、一時ファイルの掃除とレポートの退避だけは必ずやりたい」——
Jenkins の `post`（`always`）に相当する後処理を、`afterScript` に書けます。
`script` と同じコンテナ（またはシェル）内で、**`script` の成否に関わらず**最後に実行されます。

```yaml
jobs:
  test:
    script:
      - dotnet test --logger "trx;LogFileName=results.trx"
    afterScript:
      - cp -r TestResults collected-results || true   # 失敗時もレポートを回収
      - rm -rf /tmp/work                              # 一時ファイルの掃除
    artifacts:
      - collected-results
```

- **ジョブの成否は `script` だけで決まります**。`afterScript` 内のコマンドが失敗しても、ジョブの結果は変わりません。
- `afterScript` の各行は、**前の行が失敗しても続けて実行**されます（`script` の `&&` とは異なる動きです）。
- `script` が途中の `exit` で打ち切られた場合でも `afterScript` は実行されます。
- コンテナ実行・`shell` 実行・エージェントへの委譲、いずれでも使えます。
- `timeout` は `script` と `afterScript` を合わせた全体にかかります。

---

## プラグインで拡張する

**プラグイン**を追加することで、機能を後付けで拡張できます。現在の拡張点は2つです。

1. **通知チャンネル**（`INotificationChannel`）— 新しい通知先（Teams、Discord、汎用 Webhook、メールなど）
2. **ジョブランナー**（`IPluginJobRunner`）— 独自の実行バックエンド（Kubernetes、SSH、クラウドなど）

プラグインは **NuGet パッケージ**または**ローカルの .dll** として読み込めます。
YAML を直接書くほかに、**ダッシュボード（`serve`）の「プラグイン」カードから追加・削除**もできます
（設定ファイルに反映され、その場で読み込み確認まで検証されます）。

```yaml
plugins:
  - package: YourOrg.SebastianCi.Teams   # NuGet パッケージから
    version: 1.0.0
  - path: ./plugins/MyNotifier.dll       # ローカルのアセンブリから

notifications:
  - type: teams                          # プラグインが提供する type を指定
    on: [failure]
    webhook: $TEAMS_WEBHOOK
```

- **NuGet**：`package`（と任意で `version`）を指定すると、内部で一時プロジェクトをビルドして
  パッケージを取得し、アセンブリを読み込みます。取得結果は `.sebastian-ci/plugins/` にキャッシュされます。
- **ローカル**：`path` に .dll またはそれを含むディレクトリを指定します。
- 各エントリは `package` か `path` の**どちらか一方**を指定します。
- プラグインが提供する `type` の通知が、標準の通知と同じように使えます。対応するチャンネルが
  見つからない `type` は、実行時にエラーになります。

### プラグインの作り方

プラグインは、本体（`sebastian-ci`）を参照し、`SebastianCi.Core.INotificationChannel` を
**パラメーターなしのコンストラクター**で実装したクラスライブラリです。

```csharp
using SebastianCi.Core;
using SebastianCi.Models;

public sealed class TeamsNotifier : INotificationChannel
{
    public string Type => "teams";                       // notifications の type と一致させる

    public async Task SendAsync(NotificationConfig config, string message, CancellationToken ct = default)
    {
        // config.Webhook / config.Token / config.Room を使って送信する
        // …
    }
}
```

読み込み時、本体やフレームワークのアセンブリはホストと共有されるため、`INotificationChannel`
などの型はホストと同一のものとして扱われます（プラグイン固有の依存だけが隔離されます）。

### 独自のジョブランナーを足す

ジョブを「どこで・どう実行するか」を差し替えられます。プラグインで `IPluginJobRunner` を実装し、
ジョブの `runner` にその名前を指定すると、標準のコンテナ実行の代わりにそのランナーが使われます。

```yaml
plugins:
  - path: ./plugins/KubeRunner.dll

jobs:
  build:
    script: [dotnet build]      # runner 未指定 → 通常のコンテナ実行
  test:
    runner: kubernetes          # このジョブだけ独自ランナーで実行
    script: [dotnet test]
```

```csharp
using SebastianCi.Core;

public sealed class KubernetesRunner : IPluginJobRunner
{
    public string Name => "kubernetes";                  // job の runner と一致させる

    public async Task<int> RunAsync(PluginJobContext ctx, CancellationToken ct = default)
    {
        // ctx.Image / ctx.Script / ctx.Env / ctx.WorkspacePath を使って実行する
        ctx.WriteLine("Pod を作成して実行しています…");
        // …実行して終了コードを返す（0 が成功）
        return 0;
    }
}
```

- ランナーには実行に必要な情報（イメージ・スクリプト・環境変数・ワークスペースパス）が
  `PluginJobContext` で渡され、出力は `WriteLine` / `WriteError` で本体のログに流れます。
- `runner` は `agent` / `remote` とは併用できません。読み込まれていない `runner` 名は実行前にエラーになります。

---

## ゲームエンジンでの利用例（Godot / Unity / Unreal）

sebastian-ci は「コンテナの中でコマンドを実行する」だけなので、CLIでバッチビルドできる
ゲームエンジンはそのまま扱えます。これまでの機能（`timeout`・`cache`・`artifacts`・
`matrix`・`env` によるライセンス受け渡し）が、そのまま重量級のエンジンビルドに効きます。

`examples/` にすぐ使える定義ファイルを用意しています。実行前に `--validate` で構成を確認できます。

```bash
# 構成が正しいかだけ確認（コンテナは起動しない）
sebastian-ci . --validate --config examples/godot.sebastian-ci.yaml

# 実際にビルドする
sebastian-ci . --config examples/godot.sebastian-ci.yaml
```

| エンジン | 定義ファイル | ポイント |
| :--- | :--- | :--- |
| **Godot 4** | [`examples/godot.sebastian-ci.yaml`](examples/godot.sebastian-ci.yaml) | 最も手軽。`godot --headless --export-release` で書き出し。書き出しテンプレートを `cache` で再利用 |
| **Unity** | [`examples/unity.sebastian-ci.yaml`](examples/unity.sebastian-ci.yaml) | ライセンス（`.ulf`）を `$UNITY_LICENSE` でホストから渡す。`matrix` で複数プラットフォームを並列ビルド |
| **Unity (macOS / iOS)** | [`examples/unity-apple.sebastian-ci.yaml`](examples/unity-apple.sebastian-ci.yaml) | Unity エディタでの書き出しまではコンテナで、Xcode が必要な工程（.ipa 化・署名）は `shell: true` で macOS ホスト上で直接実行 |
| **Unreal Engine** | [`examples/unreal.sebastian-ci.yaml`](examples/unreal.sebastian-ci.yaml) | `RunUAT.sh BuildCookRun` でパッケージ化。イメージが巨大で重いため `timeout` を長めに、DDC を `cache` で永続化 |

**共通のコツ**

- **秘密情報はYAMLに書かない**：Unityのライセンスのように、`$UNITY_LICENSE` でホストの環境変数から受け取ります（[環境変数の受け渡し](#3-環境変数の受け渡しenv)）。
- **重いビルドには `timeout` を**：ハングしたまま無限に待たないよう、エンジンビルドには長めの制限時間を設定します。
- **`cache` はワークスペースの外に**：Unityの `Library/` やUEの `DerivedDataCache/` のように**プロジェクト内**にあるキャッシュは、ワークスペースがそのまま永続化されるため `cache` 不要です。`cache` は `~/.cache` や書き出しテンプレートなど**ワークスペース外**のパスに使います。
- **macOS / iOS の最終工程は `shell` で**：Xcode（`xcodebuild`・署名・公証）はコンテナでは動かないため、[`shell: true`](#15-コンテナを使わないホスト直接実行shell) のジョブとして macOS ホスト（または Mac 上の SlaveAgent）で直接実行します。

---

## 実行結果・ログ・成果物の保存場所

実行すると、リポジトリ直下に `.sebastian-ci/` という管理フォルダが自動で作られ、次の3種類が保存されます。
（保存先は `--data-dir` で変更できます。）

```
.sebastian-ci/
├── history.json                              # 実行履歴（いつ・成否・ログの場所）
├── logs/<コミットハッシュ>/<ジョブ名>.log      # ジョブごとの実行ログ（実行しながら随時書き込み）
├── artifacts/<コミットハッシュ>/<ジョブ名>/    # artifacts で指定した成果物
└── cache/<パス名>/                            # cache で指定したパス（コミット横断で永続）
```

### 実行履歴（`history.json`）

コミットハッシュをキーに、「実行日時・成否・ログの保存場所・ジョブごとの結果」が記録されます。

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

- **成功したコミットは、次回以降スキップされます**（`--rebuild` を付けると強制的に再実行）。
- 失敗した実行も記録されますが、こちらはスキップ対象にはならず、次回また実行されます。
- 万一 `history.json` が壊れていても、警告を出して履歴を作り直し、実行は続行します。

---

## ブラウザで使うダッシュボード（`serve`）

コマンドに不慣れな人でも使えるよう、ブラウザから操作できるダッシュボードを用意しています。
**サーバーを常駐させる必要はなく、`serve` を実行している間だけ**立ち上がります。

```bash
sebastian-ci serve /path/to/your/repo --port 8080
# → http://localhost:8080 をブラウザで開く

# トークン認証付きで起動する（localhost 以外へ公開する場合は必須）
sebastian-ci serve /path/to/your/repo --port 8080 --token my-secret
# → http://localhost:8080/?token=my-secret をブラウザで開く
```

できること:

- **リソース状況**：メモリ・ディスクの空きをメーター表示（自動更新）
- **実行履歴**：過去の実行を成否つきで一覧表示（自動更新）。**行をクリックすると詳細**（ジョブごとの成否・所要時間）が開き、各ジョブの**ログをその場で閲覧**できます
- **設定の編集**：`.sebastian-ci.yaml` を画面上で編集し、「保存して検証」で内容チェック
- **プラグインの管理**：NuGet パッケージやローカルの .dll を画面から追加・削除。設定ファイルに反映され、**その場で読み込み確認まで検証**されます
- **実行**：「実行する」ボタンでパイプラインを起動し、**ログが WebSocket でリアルタイムに流れる**

**トークン認証（`--token`）**：ダッシュボードは設定の編集・プラグインの追加・実行までできるため、
localhost の外に公開する場合は必ず `--token`（または環境変数 `SEBASTIAN_CI_SERVE_TOKEN`）で保護してください。

- ブラウザからは `http://<ホスト>:<ポート>/?token=<トークン>` で開きます。以降のAPI・WebSocket通信は HttpOnly Cookie に引き継がれるため、URLに毎回付ける必要はありません。
- API を直接叩く場合は `Authorization: Bearer <トークン>` または `X-Sebastian-Token: <トークン>` ヘッダーが使えます。
- トークンが一致しないリクエストはすべて `401` で拒否されます。トークン無しで起動すると、その旨の警告が表示されます。

内部的には、実行ボタンは `sebastian-ci` 本体を子プロセスとして起動しているだけなので、
コマンドで実行したときとまったく同じ（git連動・コンテナ・通知・履歴）動作になります。
ログは WebSocket（`/api/run/ws`）で1行ずつ配信され、接続できない環境では自動でポーリングに切り替わります。

---

## 分散実行（SlaveAgent）

重いジョブを別のマシンに肩代わりさせられます。実行させたいマシンでエージェントを起動し、
ジョブに `agent:` でそのURLを指定するだけです。

**エージェント側**（ジョブを実行するマシン）:

```bash
sebastian-ci agent /path/to/workspace --port 8771 --token my-secret
```

**マスター側**（`.sebastian-ci.yaml`）:

```yaml
jobs:
  build:
    script: [dotnet build]            # agent 未指定 → ローカルで実行
  heavy-test:
    agent: http://build-agent:8771    # このジョブだけ別マシンで実行
    agentToken: $AGENT_TOKEN          # 認証トークン（ホスト環境変数から）
    script: [dotnet test]
```

- `agent` を指定したジョブは、そのエージェントへHTTPで送られ、**エージェント側のコンテナで実行**されます。
- **ソースの同期**：マスターは現在のコミット（HEAD）のソースを `git archive` で固め、**gzip 圧縮**して実行要求に添えて送ります。エージェントはそれを展開した一時ディレクトリで実行するため、**エージェント側にリポジトリを用意しておく必要はありません**（マスターのコミット内容と一致します）。
- **差分転送（大規模リポジトリ向け）**：エージェントは受け取ったワークスペースをコミット単位でキャッシュします。2回目以降、マスターはエージェントが持つコミットを問い合わせ、**変更のあったファイルだけ**を送ります（`git diff` ベース）。削除も反映されます。全体を毎回送らないため、大きなリポジトリでも転送が高速です。
- **ログの逐次表示**：エージェントの出力は完了を待たず、**実行しながら1行ずつマスターに流れて**表示されます。
- **認証**：エージェントを `--token`（または環境変数 `SEBASTIAN_CI_AGENT_TOKEN`）で保護すると、ジョブ側の `agentToken` が一致しないリクエストは拒否されます（トークンは `$NAME` でホスト環境変数から受け取れます）。
- `agent` 未指定のジョブはローカルで実行され、両者は同じパイプライン内で混在できます。
- 接続不可・ジョブ失敗はマスター側で失敗として扱われます。`GET /agent/info` で使用エンジンとリソース状況を確認できます。
- **shell ジョブとの組み合わせ**：`shell: true` のジョブを委譲すると、**エージェントのホスト上で直接実行**されます。Mac でエージェントを起動しておけば、Linux / Windows のマスターから iOS の `.ipa` 化や macOS の署名といった Xcode 工程を組み込めます。コンテナエンジン（podman / docker）の無いマシンでも、エージェントは **shell ジョブ専用として起動できます**。

### エージェントプールによる自動負荷分散

エージェントを複数用意して `agents` に列挙し、ジョブに `remote: true` を付けると、
**最も空いている（実行中ジョブ数が最少の）エージェントに自動で割り当て**られます。

```yaml
agents:
  - url: http://agent-1:8771
    token: $AGENT_TOKEN
  - url: http://agent-2:8771
    token: $AGENT_TOKEN

jobs:
  test-a: { remote: true, script: [dotnet test A] }   # プールの空いている方へ
  test-b: { remote: true, script: [dotnet test B] }   # もう片方へ
```

- **負荷分散**：`remote: true` のジョブは、その時点で最も空いているエージェントに割り当てられます。並列に走る複数ジョブが自動的に振り分けられます。
- **フェイルオーバー**：割り当て先に接続できなかった場合、次に空いているエージェントへ自動で切り替えます。全滅した場合のみ失敗になります。
- 特定のエージェントに固定したいジョブは、従来どおり `agent: <url>` で明示指定できます（`remote` とは併用不可）。

---

## 中断してもコンテナが残らない仕組み

CIの実行中に処理を止めたとき、C#のプロセスだけが終わって**コンテナがゾンビのように残り続ける**——
これはローカルCIでありがちなトラブルです。sebastian-ci はこれを防ぐため、どの止め方でも起動中のコンテナを確実に片付けます。

- 各コンテナは `sebastian-ci-<ジョブ名>-<ランダムな8文字>` という一意な名前で起動し、実行中は内部で一覧管理されています。
- **`Ctrl+C`（SIGINT）で中断**した場合：即座に強制終了せず、実行中のジョブへ停止を伝えたうえで、起動中の全コンテナに `stop` と `rm` を並列で送信してから終了します（終了コード `130`）。
- **`kill`（SIGTERM）や予期しないエラー**で終了する場合も、最終防衛線として同じ後片付けを実行してから終了します（SIGTERM の終了コード `143`）。
- すでに完走したジョブのコンテナ（`--rm` で自動削除済み）は対象外です。後片付けはどの経路でも一度だけ行われます。

---

## コマンドラインオプション一覧

```
sebastian-ci [リポジトリパス] [オプション...]
```

| オプション | 説明 |
| :--- | :--- |
| `リポジトリパス` | 対象のGitリポジトリ（省略時はカレントディレクトリ） |
| `--rebuild` | 実行済みのコミットでも、スキップせず強制的に再実行する |
| `--validate` | ジョブを実行せず、構成ファイルの正しさだけを確認する |
| `--job <ジョブ名>` | 指定したジョブとその依存だけを実行する（複数回指定可） |
| `--param <名前=値>` | `params` 定義のパラメーターに値を渡す（複数回指定可） |
| `--yes` | `approval` の承認ゲートをすべて自動承認する |
| `--max-parallel <N>` | 同時に実行するコンテナ数の上限（既定：無制限） |
| `--config <ファイル名>` | 読み込む定義ファイルを変更する（既定：`.sebastian-ci.yaml`） |
| `--data-dir <パス>` | 履歴・ログ・成果物の保存先を変更する（既定：`<リポジトリ>/.sebastian-ci`） |
| `--engine <podman\|docker>` | 使うコンテナエンジンを明示指定する（既定：自動検出） |

### サブコマンド

| コマンド | 説明 |
| :--- | :--- |
| `serve [パス] [--port N] [--token T]` | ブラウザ用ダッシュボードを起動する（`--token` で認証を有効化） |
| `agent [パス] [--port N] [--token T]` | 分散実行のエージェント（ジョブ受け付け）を起動する |

---

## よくある質問（FAQ）

**Q. Docker Desktop がなくても使えますか？**
はい。Podman があれば動きます。Podman と Docker のどちらか一方があれば十分で、両方あれば Podman が優先されます。

**Q. なぜ同じコミットだと実行がスキップされるのですか？**
一度成功した内容を無駄に再実行しないためです。コードを変えずに何度も試したいときは `--rebuild` を付けてください。

**Q. `pre-commit` フックに組み込むには？**
`.git/hooks/pre-commit` から `sebastian-ci . --job test` のように呼び出すのが手軽です。特定ジョブだけを素早く回せます。終了コードが `0` 以外ならコミットが中止されます。

**Q. macOS 向けや iOS 向けの Unity ビルドはできますか？**
はい。Unity エディタでの書き出し（macOS 向け Mono ビルド、iOS 向け Xcode プロジェクト生成）まではコンテナで実行でき、Xcode が必要な工程（`.ipa` 化・署名・公証）は `shell: true` のジョブとして macOS ホスト、または Mac 上の SlaveAgent で直接実行します。[`examples/unity-apple.sebastian-ci.yaml`](examples/unity-apple.sebastian-ci.yaml) を参照してください。

**Q. Podman を使っているのに、コンテナからファイルが読めません。**
SELinux が有効な環境（Fedora など）で起きることがありますが、sebastian-ci は Podman 使用時にマウントへ自動で `:Z` を付けるため、通常はそのまま動きます。

**Q. コンテナはどのように実行されていますか？**
各ジョブは次のような使い捨てコンテナとして実行されます（Podman / Docker 共通）。リポジトリはコンテナ内の `/workspace` にマウントされ、そこが作業ディレクトリになります。

```
<podman|docker> run --rm \
  -e KEY=VALUE ... \
  --volume <リポジトリの絶対パス>:/workspace[:Z] \
  --workdir /workspace \
  <イメージ> /bin/sh -c "<script を && でつないだもの>"
```

コマンドの引数は1つずつ個別に渡しているため、パスに空白や特殊文字が含まれても意図しない解釈（シェルインジェクション）は起こりません。

**Q. 設定ミスはどう教えてくれますか？**
実行前にまとめて検証され、問題があればわかりやすいメッセージで停止します。検出される主な例：

- `needs` の循環（ループ）や、自分自身・存在しないジョブへの依存
- `image` や `script` の指定漏れ、空のコマンド
- `stages` の名前の重複・空、未定義ステージの指定、後のステージへの `needs`
- `matrix` の組み合わせが上限（50）を超える、`env` が参照するホスト変数が未定義 など

---

## アーキテクチャ

「1クラス1責務」を徹底し、それぞれの部品が独立してテスト・理解しやすいように分割しています。

| クラス | 責務 |
| :--- | :--- |
| `Program` | CLI 引数の解析と全体の制御フロー |
| `CliOptions` | コマンドライン引数の解析 |
| `GitManager` | git コマンドの実行（コミットハッシュ取得・変更ファイル一覧の取得） |
| `PipelineParser` | `.sebastian-ci.yaml` の読み込み・検証・正規化 |
| `EnvironmentVariableExpander` | `env` の値に含まれる `$NAME` / `${NAME}` をホスト環境変数で展開 |
| `MatrixExpander` | `matrix` ジョブの全組み合わせ展開と `needs` の張り直し |
| `ChangeDetector` | 前回成功コミットとの差分とグロブパターンによる実行要否の判定 |
| `JobSelector` | `--job` 指定ジョブとその依存への絞り込み |
| `DependencyGraph` | 依存関係（`needs` ＋ステージ）の構築とトポロジカルソート |
| `DagEngine` | 依存関係にもとづくジョブの並列実行制御 |
| `ContainerEngine` | Podman / Docker の差異の吸収と、利用可能なエンジンの自動検出 |
| `ContainerRunner` | コンテナの起動・実行とログのリアルタイム回収 |
| `ShellRunner` | コンテナを使わないホスト直接実行（`shell: true`）とログのリアルタイム回収 |
| `ActiveContainerRegistry` | 実行中コンテナのスレッドセーフな管理 |
| `ContainerCleanup` | 中断時の全コンテナの停止（`stop -t 2`）と削除（`rm`） |
| `HistoryManager` | `history.json` への実行履歴の保存・更新と、スキップ判定 |
| `ArtifactManager` | 成果物の保存先への退避 |
| `SystemResourceMonitor` | メモリ・ディスクの状況取得としきい値による警告 |
| `NotificationDispatcher` | イベント（`on`）に一致する通知先への配信 |
| `SlackNotifier` / `ChatWorkNotifier` | Slack / ChatWork への HTTP 送信 |
| `PluginLoader` / `PluginLoadContext` | プラグインの解決・読み込みと拡張点の発見 |
| `NuGetPluginResolver` | NuGet パッケージをローカルアセンブリへ解決 |
| `JobRunnerSelector` | ジョブの指定に応じてローカル／直接エージェント／プールを選択 |
| `RemoteAgentRunner` | 指定エージェントへの HTTP 委譲と NDJSON ストリーム受信 |
| `AgentPool` / `PooledAgentRunner` | 複数エージェントへの負荷分散とフェイルオーバー |
| `WebServer` / `AgentServer` | ダッシュボード / 分散実行エージェントのHTTPサーバー |
| `PathSanitizer` | ジョブ名をファイル名として安全な形へ変換 |
| `ConsoleLogger` | スレッドセーフな色付きコンソール出力 |
