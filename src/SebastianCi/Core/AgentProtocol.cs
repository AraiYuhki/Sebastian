namespace SebastianCi.Core;

/// <summary>
/// マスターからエージェントへ渡すジョブ実行要求（コンテナ実行に必要な最小情報）。
/// </summary>
public sealed record AgentJobRequest(
    string JobId,
    string Image,
    List<string> Script,
    Dictionary<string, string> Env,
    int Timeout,
    List<string> Cache);

/// <summary>
/// エージェントからマスターへ返すジョブ実行結果（終了コードと出力行）。
/// </summary>
public sealed record AgentJobResponse(int ExitCode, List<string> Log);
