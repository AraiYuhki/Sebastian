namespace SebastianCi.Models;

/// <summary>
/// .sebastian-ci.yaml の jobs 配下1件分のスキーマを表す。
/// <code>
/// image:  ジョブ固有のPodmanイメージ名（省略時はグローバル image を継承）
/// stage:  所属ステージ名（stages 定義時は必須、未定義時は指定禁止）
/// needs:  依存する先行ジョブIDの配列（任意・DAG解析用）
/// script: コンテナ内で実行するコマンドの配列（必須）
/// env:    ジョブ固有の環境変数（同名キーはグローバル env を上書き）
/// artifacts: ジョブ成功後に退避する成果物のパス配列（任意・ワークスペース相対）
/// matrix: 変数名→値リストのマップ（任意）。全組み合わせにジョブが展開される。
///         特別なキー image はコンテナイメージを切り替え、他のキーは環境変数として注入される
/// changes: 変更検知用のグロブパターン配列（任意）。前回成功コミットとの差分が
///          いずれのパターンにも一致しない場合、このジョブはスキップされる
/// timeout: ジョブの制限時間（秒・任意）。0 は無制限。超過するとコンテナを停止して失敗扱いにする
/// retry:   失敗時の再試行回数（任意）。0 は再試行なし
/// continueOnError: true の場合、失敗しても後続ジョブとパイプライン全体の成否に影響させない（任意）
/// cache:   コミットをまたいで永続化するコンテナ内パス配列（任意・絶対パス）。実行の高速化に使う
/// </code>
/// </summary>
public sealed class JobDefinition
{
    /// <summary>ジョブ固有のコンテナイメージ名。空の場合はパース後の正規化でグローバル image が設定される。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>所属するステージ名。stages が定義されている場合は必須。</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>このジョブが依存する先行ジョブのID一覧。</summary>
    public List<string> Needs { get; set; } = new();

    /// <summary>コンテナ内で順番に実行するシェルコマンド。</summary>
    public List<string> Script { get; set; } = new();

    /// <summary>
    /// script の成否に関わらず、同じコンテナ（またはシェル）内で最後に実行される後処理コマンド。
    /// 一時ファイルの削除やレポートの退避などに使う。後処理の失敗はジョブの成否に影響しない。
    /// </summary>
    public List<string> AfterScript { get; set; } = new();

    /// <summary>ジョブの環境変数。パース後の正規化でグローバル env とマージ済みになる。</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>ジョブ成功後に退避する成果物のパス。ワークスペース相対で指定する。</summary>
    public List<string> Artifacts { get; set; } = new();

    /// <summary>
    /// JUnit XML 形式のテストレポートを探すグロブパターン（ワークスペース相対）。
    /// 指定すると、ジョブの成否に関わらず実行後に解析され、失敗テストのサマリーが表示・記録される。
    /// </summary>
    public List<string> Reports { get; set; } = new();

    /// <summary>マトリックスビルドの変数名→値リスト。パース後の展開で全組み合わせのジョブが生成される。</summary>
    public Dictionary<string, List<string>> Matrix { get; set; } = new();

    /// <summary>変更検知用のグロブパターン。差分が一致しない場合はジョブをスキップする。</summary>
    public List<string> Changes { get; set; } = new();

    /// <summary>ジョブの制限時間（秒）。0 は無制限。超過時はコンテナを停止して失敗扱いにする。</summary>
    public int Timeout { get; set; }

    /// <summary>失敗時の再試行回数。0 は再試行なし。</summary>
    public int Retry { get; set; }

    /// <summary>true の場合、失敗しても後続ジョブとパイプライン全体の成否に影響させない。</summary>
    public bool ContinueOnError { get; set; }

    /// <summary>コミットをまたいで永続化するコンテナ内パス（絶対パス）。実行の高速化に使う。</summary>
    public List<string> Cache { get; set; } = new();

    /// <summary>このジョブを実行するリモートエージェントのURL。空ならローカルで実行する。</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>リモートエージェントの認証トークン。$NAME でホスト環境変数を参照できる。</summary>
    public string AgentToken { get; set; } = string.Empty;

    /// <summary>true の場合、エージェントプール（pipeline の agents）の中で最も空いているエージェントで実行する。</summary>
    public bool Remote { get; set; }

    /// <summary>
    /// remote 指定時に、割り当て先エージェントへ要求する能力ラベル（例: macos / xcode）。
    /// 指定したラベルをすべて持つエージェントの中から、最も空いているものが選ばれる。
    /// </summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>プラグインが提供するジョブランナーの名前。指定するとそのランナーで実行する。</summary>
    public string Runner { get; set; } = string.Empty;

    /// <summary>
    /// 実行前の手動承認ゲートのメッセージ。空でない場合、このジョブは実行前にコンソールでの承認
    /// （y 入力）を待ち、承認されなければ失敗として扱われる。--yes 指定時は自動承認される。
    /// </summary>
    public string Approval { get; set; } = string.Empty;

    /// <summary>
    /// true の場合、コンテナを使わずホストマシン上で script を直接実行する。
    /// Xcode のようにコンテナ化できないツールチェーン（macOS / iOS ビルド等）向け。
    /// image / cache とは併用できない。agent / remote と併用すると、エージェント側のホストで直接実行される。
    /// </summary>
    public bool Shell { get; set; }
}
