namespace NextCi.Models;

/// <summary>
/// .next-ci.yaml に定義された1ジョブ分の設定を保持する。
/// </summary>
public sealed class JobDefinition
{
    /// <summary>ジョブを実行するコンテナイメージ名。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>コンテナ内で順番に実行するシェルコマンド。</summary>
    public List<string> Commands { get; set; } = new();

    /// <summary>このジョブが依存する先行ジョブのID一覧。</summary>
    public List<string> Needs { get; set; } = new();
}
