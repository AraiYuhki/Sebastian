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

    /// <summary>ジョブの環境変数。パース後の正規化でグローバル env とマージ済みになる。</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>ジョブ成功後に退避する成果物のパス。ワークスペース相対で指定する。</summary>
    public List<string> Artifacts { get; set; } = new();

    /// <summary>マトリックスビルドの変数名→値リスト。パース後の展開で全組み合わせのジョブが生成される。</summary>
    public Dictionary<string, List<string>> Matrix { get; set; } = new();

    /// <summary>変更検知用のグロブパターン。差分が一致しない場合はジョブをスキップする。</summary>
    public List<string> Changes { get; set; } = new();
}
