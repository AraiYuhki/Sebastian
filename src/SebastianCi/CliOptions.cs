using SebastianCi.Core;

namespace SebastianCi;

/// <summary>
/// CLI引数（リポジトリパス・--rebuild・--config・--data-dir）の解析結果を保持する。
/// </summary>
public sealed record CliOptions(
    string RepositoryPath, bool IsRebuildRequired, string ConfigFileName, string? DataDirectoryPath)
{
    /// <summary>引数を解析する。解釈できない引数があった場合は null を返す。</summary>
    public static CliOptions? Parse(string[] args)
    {
        string repositoryPath = ".";
        bool isRebuildRequired = false;
        string configFileName = PipelineParser.DefaultConfigFileName;
        string? dataDirectoryPath = null;

        Queue<string> remainingArgs = new(args);
        while (remainingArgs.TryDequeue(out string? argument))
        {
            if (!TryApplyArgument(
                argument, remainingArgs,
                ref repositoryPath, ref isRebuildRequired, ref configFileName, ref dataDirectoryPath))
            {
                return null;
            }
        }

        return new CliOptions(repositoryPath, isRebuildRequired, configFileName, dataDirectoryPath);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("次世代ローカルCIエンジン sebastian-ci");
        Console.WriteLine();
        Console.WriteLine("使い方:");
        Console.WriteLine("  sebastian-ci [リポジトリパス] [--rebuild] [--config <ファイル名>] [--data-dir <パス>]");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  リポジトリパス       対象のGitリポジトリ (省略時はカレントディレクトリ)");
        Console.WriteLine("  --rebuild            実行済みコミットでも強制的に再実行する");
        Console.WriteLine($"  --config <ファイル名>  パイプライン定義ファイル (既定: {PipelineParser.DefaultConfigFileName})");
        Console.WriteLine($"  --data-dir <パス>     履歴・ログ・成果物の保存先 (既定: <リポジトリ>/{HistoryManager.DefaultDataDirectoryName})");
    }

    private static bool TryApplyArgument(
        string argument, Queue<string> remainingArgs,
        ref string repositoryPath, ref bool isRebuildRequired, ref string configFileName, ref string? dataDirectoryPath)
    {
        if (argument is "--rebuild")
        {
            isRebuildRequired = true;
            return true;
        }

        if (argument is "--config") return remainingArgs.TryDequeue(out configFileName!);
        if (argument is "--data-dir") return remainingArgs.TryDequeue(out dataDirectoryPath);
        if (argument.StartsWith('-')) return false;

        repositoryPath = argument;
        return true;
    }
}
