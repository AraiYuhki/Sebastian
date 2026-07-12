namespace SebastianCi.Core;

/// <summary>
/// マスターからエージェントへ渡すジョブ実行要求（コンテナ実行に必要な最小情報）。
/// WorkspaceTarBase64 が指定された場合、エージェントはそれを展開した一時ディレクトリで実行する。
/// </summary>
public sealed record AgentJobRequest(
    string JobId,
    string Image,
    List<string> Script,
    Dictionary<string, string> Env,
    int Timeout,
    List<string> Cache,
    string? WorkspaceTarBase64 = null);

/// <summary>
/// エージェントからマスターへ返すジョブ実行結果（終了コードと出力行）。
/// </summary>
public sealed record AgentJobResponse(int ExitCode, List<string> Log);
