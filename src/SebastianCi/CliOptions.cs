using SebastianCi.Core;

namespace SebastianCi;

/// <summary>
/// CLI引数（リポジトリパス・--rebuild・--config・--data-dir・--engine・--job・--max-parallel・--param）の解析結果を保持する。
/// </summary>
public sealed record CliOptions(
    string RepositoryPath,
    bool IsRebuildRequired,
    string ConfigFileName,
    string? DataDirectoryPath,
    string? EngineName,
    IReadOnlyList<string> TargetJobIds,
    int? MaxParallel,
    bool IsValidateOnly,
    IReadOnlyDictionary<string, string> Parameters,
    bool AutoApprove)
{
    private static readonly string[] SupportedEngineNames = ["podman", "docker"];

    /// <summary>引数を解析する。解釈できない引数があった場合は null を返す。</summary>
    public static CliOptions? Parse(string[] args)
    {
        MutableOptions state = new();
        Queue<string> remainingArgs = new(args);
        while (remainingArgs.TryDequeue(out string? argument))
        {
            if (!TryApplyArgument(argument, remainingArgs, state)) return null;
        }

        return new CliOptions(
            state.RepositoryPath, state.IsRebuildRequired, state.ConfigFileName,
            state.DataDirectoryPath, state.EngineName, state.TargetJobIds, state.MaxParallel, state.IsValidateOnly,
            state.Parameters, state.AutoApprove);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("次世代ローカルCIエンジン sebastian-ci");
        Console.WriteLine();
        Console.WriteLine("使い方:");
        Console.WriteLine("  sebastian-ci [リポジトリパス] [--rebuild] [--job <ジョブID>]...");
        Console.WriteLine("               [--config <ファイル名>] [--data-dir <パス>] [--engine <podman|docker>]");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  リポジトリパス       対象のGitリポジトリ (省略時はカレントディレクトリ)");
        Console.WriteLine("  --rebuild            実行済みコミットでも強制的に再実行する");
        Console.WriteLine("  --validate           構成ファイルの検証のみ行い、ジョブは実行しない");
        Console.WriteLine("  --job <ジョブID>      指定ジョブとその依存ジョブのみ実行する (複数指定可)");
        Console.WriteLine("  --param <名前=値>     params 定義のパラメーターに値を渡す (複数指定可)");
        Console.WriteLine("  --yes                approval の承認ゲートをすべて自動承認する");
        Console.WriteLine("  --max-parallel <N>   同時に実行するコンテナ数の上限 (既定: 無制限)");
        Console.WriteLine($"  --config <ファイル名>  パイプライン定義ファイル (既定: {PipelineParser.DefaultConfigFileName})");
        Console.WriteLine($"  --data-dir <パス>     履歴・ログ・成果物の保存先 (既定: <リポジトリ>/{HistoryManager.DefaultDataDirectoryName})");
        Console.WriteLine("  --engine <名前>       使用するコンテナエンジン (既定: podman → docker の順で自動検出)");
    }

    private static bool TryApplyArgument(string argument, Queue<string> remainingArgs, MutableOptions state)
    {
        if (argument is "--rebuild")
        {
            state.IsRebuildRequired = true;
            return true;
        }

        if (argument is "--validate")
        {
            state.IsValidateOnly = true;
            return true;
        }

        if (argument is "--yes")
        {
            state.AutoApprove = true;
            return true;
        }

        if (argument is "--config") return TryDequeueValue(remainingArgs, value => state.ConfigFileName = value);
        if (argument is "--data-dir") return TryDequeueValue(remainingArgs, value => state.DataDirectoryPath = value);
        if (argument is "--engine") return TryDequeueEngineName(remainingArgs, state);
        if (argument is "--job") return TryDequeueValue(remainingArgs, state.TargetJobIds.Add);
        if (argument is "--param") return TryDequeueParameter(remainingArgs, state);
        if (argument is "--max-parallel") return TryDequeueMaxParallel(remainingArgs, state);
        if (argument.StartsWith('-')) return false;

        state.RepositoryPath = argument;
        return true;
    }

    private static bool TryDequeueEngineName(Queue<string> remainingArgs, MutableOptions state)
        => TryDequeueValue(remainingArgs, value => state.EngineName = value)
            && SupportedEngineNames.Contains(state.EngineName);

    /// <summary>--param の値を「名前=値」として解析する。名前が空・= が無い形式は受け付けない。</summary>
    private static bool TryDequeueParameter(Queue<string> remainingArgs, MutableOptions state)
    {
        if (!remainingArgs.TryDequeue(out string? value)) return false;

        int separatorIndex = value.IndexOf('=');
        if (separatorIndex < 1) return false;

        state.Parameters[value[..separatorIndex]] = value[(separatorIndex + 1)..];
        return true;
    }

    private static bool TryDequeueMaxParallel(Queue<string> remainingArgs, MutableOptions state)
    {
        if (!remainingArgs.TryDequeue(out string? value)) return false;
        if (!int.TryParse(value, out int maxParallel) || maxParallel < 1) return false;

        state.MaxParallel = maxParallel;
        return true;
    }

    private static bool TryDequeueValue(Queue<string> remainingArgs, Action<string> apply)
    {
        if (!remainingArgs.TryDequeue(out string? value)) return false;

        apply(value);
        return true;
    }

    /// <summary>解析中の値を一時的に保持する内部状態。</summary>
    private sealed class MutableOptions
    {
        public string RepositoryPath { get; set; } = ".";
        public bool IsRebuildRequired { get; set; }
        public string ConfigFileName { get; set; } = PipelineParser.DefaultConfigFileName;
        public string? DataDirectoryPath { get; set; }
        public string? EngineName { get; set; }
        public List<string> TargetJobIds { get; } = new();
        public Dictionary<string, string> Parameters { get; } = new();
        public int? MaxParallel { get; set; }
        public bool IsValidateOnly { get; set; }
        public bool AutoApprove { get; set; }
    }
}
