# コントリビューションガイド

sebastian-ci への貢献をありがとうございます。このドキュメントは、開発環境の構築・
ビルド・テスト・コーディング規約・変更の提出方法をまとめたものです。

## 開発に必要なもの

| ツール | 用途 |
| :--- | :--- |
| [.NET 8 SDK](https://dotnet.microsoft.com/) | ビルド・テスト・実行 |
| Git | バージョン管理・変更検知 |
| [Podman](https://podman.io/) または [Docker](https://www.docker.com/) | ジョブ実行の動作確認（任意） |

## ビルドとテスト

リポジトリのルートで、ソリューション単位のコマンドが使えます。

```bash
# 依存の復元とビルド
dotnet build SebastianCi.sln -c Release

# テストの実行（xUnit）
dotnet test

# CLI をそのまま試す
dotnet run --project src/SebastianCi -- /path/to/repo
```

## プロジェクト構成

```
src/SebastianCi/          本体（CLI・Core・Web・Models）
  ├─ Core/                実行エンジン・Git・コンテナ・通知・エージェント等
  ├─ Web/                 ダッシュボード / 分散エージェントの HTTP サーバー
  └─ Models/              YAML スキーマと結果のデータ型
tests/SebastianCi.Tests/  xUnit ユニットテスト
examples/                 Godot / Unity / Unreal のサンプル定義
docs/                     製品紹介ページ
```

責務は「1クラス1責務」で分割しています。全体像は README の「アーキテクチャ」節を参照してください。

## コーディング規約

本プロジェクトは、読みやすさと一貫性のために次の規約を守っています。

**命名**
- クラス / メソッド / プロパティ：PascalCase（メソッドは動詞始まり、非同期は `Async` で終える）
- ローカル変数 / 引数：camelCase（`x` や `tmp` のような無意味名は避ける）
- プライベートフィールド：`_camelCase`
- インターフェース：`IPascalCase`

**モダン C# 構文**
- File-scoped namespaces（`namespace Foo;`）
- Target-typed new（`Dictionary<string, Job> jobs = new();`）
- 式形式メンバー・パターンマッチングで簡潔に

**非同期処理**
- 外部プロセス・ファイル I/O・ネットワークは `async/await`
- 戻り値は `Task` / `Task<T>`（イベントハンドラーを除き `async void` 禁止）

**設計**
- 1メソッドは原則30行以内、ネストは最大2階層（超える場合は抽出）
- 早期リターンで巨大な if-else を避ける
- 例外を握り潰さない（最低限ログ、または再スロー）
- 外部プロセスは終了コードを検証し、異常時は専用例外をスロー

## 変更を提出する

1. `master` から作業ブランチを切ります。
2. 変更を加え、**`dotnet test` が通ること**と `dotnet build -c Release` が警告なく通ることを確認します。
3. ふるまいを変えた場合は、対応するユニットテストを追加します。
4. コミットメッセージは「何を・なぜ」が分かる簡潔なものにします。
5. プルリクエストを作成します（テンプレートが表示されます）。

CI（GitHub Actions）でビルドとテストが自動実行されます。緑になっていることを確認してください。
