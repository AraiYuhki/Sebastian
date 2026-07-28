namespace SebastianCi.Models;

/// <summary>
/// エージェントプールの1エントリ（エージェントのURL・認証トークン・能力ラベル）。
/// token は $NAME でホスト環境変数を参照できる。
/// </summary>
public sealed class AgentEndpoint
{
    /// <summary>エージェントのURL（例: http://build-agent:8771）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>エージェントの認証トークン（任意）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// このエージェントが持つ能力を表すラベル（例: macos / xcode / gpu）。
    /// ジョブ側の labels をすべて含むエージェントだけが割り当て候補になる。
    /// </summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>ジョブが要求するラベルをすべて備えているかどうか。</summary>
    public bool Satisfies(IReadOnlyList<string> requiredLabels)
        => requiredLabels.All(label => Labels.Contains(label, StringComparer.Ordinal));
}
