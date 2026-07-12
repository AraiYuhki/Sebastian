namespace SebastianCi.Web;

/// <summary>
/// `agent` サブコマンドの引数（ワークスペースのパス・--port・--engine）の解析結果。
/// </summary>
public sealed record AgentOptions(string RepositoryPath, int Port, string? EngineName, string? Token)
{
    public const int DefaultPort = 8771;

    public static AgentOptions? Parse(string[] args)
    {
        string repositoryPath = ".";
        int port = DefaultPort;
        string? engineName = null;
        string? token = Environment.GetEnvironmentVariable("SEBASTIAN_CI_AGENT_TOKEN");

        Queue<string> remaining = new(args);
        while (remaining.TryDequeue(out string? argument))
        {
            if (!TryApply(argument, remaining, ref repositoryPath, ref port, ref engineName, ref token)) return null;
        }

        return new AgentOptions(repositoryPath, port, engineName, token);
    }

    private static bool TryApply(
        string argument, Queue<string> remaining,
        ref string repositoryPath, ref int port, ref string? engineName, ref string? token)
    {
        if (argument is "--port") return TryDequeuePort(remaining, ref port);
        if (argument is "--engine") return remaining.TryDequeue(out engineName);
        if (argument is "--token") return remaining.TryDequeue(out token);
        if (argument.StartsWith('-')) return false;

        repositoryPath = argument;
        return true;
    }

    private static bool TryDequeuePort(Queue<string> remaining, ref int port)
    {
        if (!remaining.TryDequeue(out string? value)) return false;
        if (!int.TryParse(value, out int parsed) || parsed is < 1 or > 65535) return false;

        port = parsed;
        return true;
    }
}
