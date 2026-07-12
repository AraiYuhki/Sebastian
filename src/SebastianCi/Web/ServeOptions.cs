using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>
/// `serve` サブコマンドの引数（リポジトリパス・--port・--data-dir・--config）の解析結果。
/// </summary>
public sealed record ServeOptions(string RepositoryPath, int Port, string? DataDirectoryPath, string ConfigFileName)
{
    public const int DefaultPort = 8080;

    /// <summary>`serve` を除いた引数列を解析する。解釈できない場合は null を返す。</summary>
    public static ServeOptions? Parse(string[] args)
    {
        string repositoryPath = ".";
        int port = DefaultPort;
        string? dataDirectoryPath = null;
        string configFileName = PipelineParser.DefaultConfigFileName;

        Queue<string> remaining = new(args);
        while (remaining.TryDequeue(out string? argument))
        {
            if (!TryApply(argument, remaining, ref repositoryPath, ref port, ref dataDirectoryPath, ref configFileName))
            {
                return null;
            }
        }

        return new ServeOptions(repositoryPath, port, dataDirectoryPath, configFileName);
    }

    private static bool TryApply(
        string argument, Queue<string> remaining,
        ref string repositoryPath, ref int port, ref string? dataDirectoryPath, ref string configFileName)
    {
        if (argument is "--port") return TryDequeuePort(remaining, ref port);
        if (argument is "--data-dir") return remaining.TryDequeue(out dataDirectoryPath);
        if (argument is "--config") return remaining.TryDequeue(out configFileName!);
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
