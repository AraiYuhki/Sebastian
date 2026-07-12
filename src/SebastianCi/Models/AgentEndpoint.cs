namespace SebastianCi.Models;

/// <summary>
/// エージェントプールの1エントリ（エージェントのURLと認証トークン）。
/// token は $NAME でホスト環境変数を参照できる。
/// </summary>
public sealed class AgentEndpoint
{
    /// <summary>エージェントのURL（例: http://build-agent:8771）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>エージェントの認証トークン（任意）。</summary>
    public string Token { get; set; } = string.Empty;
}
