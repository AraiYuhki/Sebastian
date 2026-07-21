namespace SebastianCi.Core;

/// <summary>
/// マスターからエージェントへ渡すジョブ実行要求（コンテナ実行に必要な最小情報）。
/// WorkspaceTarBase64（gzip 圧縮した tar の base64）が指定された場合、エージェントはそれを
/// 展開した一時ディレクトリで実行する。BaseCommitHash が指定されていれば差分転送で、
/// エージェントはキャッシュ済みの Base ワークスペースに変更分を適用してから実行する。
/// Shell が true の場合、エージェントはコンテナを使わずホスト上で script を直接実行する。
/// </summary>
public sealed record AgentJobRequest(
    string JobId,
    string Image,
    List<string> Script,
    Dictionary<string, string> Env,
    int Timeout,
    List<string> Cache,
    string? WorkspaceTarBase64 = null,
    string? CommitHash = null,
    string? BaseCommitHash = null,
    List<string>? DeletedPaths = null,
    bool Shell = false);

/// <summary>エージェントがキャッシュ済みのコミット一覧（差分転送のベース候補）。</summary>
public sealed record AgentCommitsResponse(List<string> Commits);

/// <summary>
/// エージェントからマスターへ返すジョブ実行結果（終了コードと出力行）。
/// </summary>
public sealed record AgentJobResponse(int ExitCode, List<string> Log);

/// <summary>
/// エージェントの逐次ストリーミング（NDJSON）1行分。出力行なら Line、
/// 実行完了なら ExitCode がセットされる（両方 null のことはない）。
/// </summary>
public sealed record AgentStreamMessage(string? Line = null, int? ExitCode = null);
