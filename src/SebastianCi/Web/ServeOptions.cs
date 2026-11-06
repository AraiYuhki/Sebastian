using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>
/// `serve` サブコマンドの引数（リポジトリパス・--port・--data-dir・--config・--token）の解析結果。
/// Token は --token または環境変数 SEBASTIAN_CI_SERVE_TOKEN で指定できる（null なら認証なし）。
/// </summary>
public sealed record ServeOptions(
    string RepositoryPath, int Port, string? DataDirectoryPath, string ConfigFileName, string? Token = null,
    bool Hub = false)
{
    public const int DefaultPort = 8080;
    public const string TokenEnvironmentVariableName = "SEBASTIAN_CI_SERVE_TOKEN";
    public const string DefaultHubDirectoryName = ".sebastian-ci-hub";

    /// <summary>ハブ（複数リポジトリ）の既定の保存先。ユーザーのホーム直下に置き、どこから起動しても同じ登録を使う。</summary>
    public static string DefaultHubDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultHubDirectoryName);

    /// <summary>データ保存先（.sebastian-ci）の絶対パス。--data-dir 未指定ならリポジトリ配下。</summary>
    public string ResolveDataRootPath()
        => DataDirectoryPath is not null
            ? Path.GetFullPath(DataDirectoryPath)
            : Hub
                ? DefaultHubDirectory
                : Path.Combine(Path.GetFullPath(RepositoryPath), HistoryManager.DefaultDataDirectoryName);

    /// <summary>
    /// 子プロセスの `serve` に渡す引数列を組み立てる。
    /// トークンはプロセス一覧に露出しないよう引数に含めない（環境変数で渡す）。
    /// </summary>
    public IReadOnlyList<string> ToServeArguments()
    {
        List<string> arguments = Hub
            ? ["--hub", "--port", Port.ToString(), "--config", ConfigFileName]
            : [Path.GetFullPath(RepositoryPath), "--port", Port.ToString(), "--config", ConfigFileName];
        if (DataDirectoryPath is not null) arguments.AddRange(["--data-dir", Path.GetFullPath(DataDirectoryPath)]);
        return arguments;
    }

    /// <summary>`serve` を除いた引数列を解析する。解釈できない場合は null を返す。</summary>
    public static ServeOptions? Parse(string[] args)
    {
        string repositoryPath = ".";
        int port = DefaultPort;
        string? dataDirectoryPath = null;
        string configFileName = PipelineParser.DefaultConfigFileName;
        string? token = Environment.GetEnvironmentVariable(TokenEnvironmentVariableName);
        bool hub = false;

        Queue<string> remaining = new(args);
        while (remaining.TryDequeue(out string? argument))
        {
            if (!TryApply(
                argument, remaining, ref repositoryPath, ref port, ref dataDirectoryPath, ref configFileName,
                ref token, ref hub))
            {
                return null;
            }
        }

        return new ServeOptions(
            repositoryPath, port, dataDirectoryPath, configFileName,
            string.IsNullOrWhiteSpace(token) ? null : token, hub);
    }

    private static bool TryApply(
        string argument, Queue<string> remaining,
        ref string repositoryPath, ref int port, ref string? dataDirectoryPath, ref string configFileName,
        ref string? token, ref bool hub)
    {
        if (argument is "--hub")
        {
            hub = true;
            return true;
        }

        if (argument is "--port") return TryDequeuePort(remaining, ref port);
        if (argument is "--data-dir") return remaining.TryDequeue(out dataDirectoryPath);
        if (argument is "--config") return remaining.TryDequeue(out configFileName!);
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
