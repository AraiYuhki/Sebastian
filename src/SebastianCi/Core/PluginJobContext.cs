namespace SebastianCi.Core;

/// <summary>
/// プラグインのジョブランナーに渡す実行コンテキスト。ジョブの実行に必要な情報と、
/// 出力を本体（コンソール表示・ログ保存）へ届けるための書き込み口を提供する。
/// </summary>
public sealed class PluginJobContext
{
    private readonly Action<string, bool> _output;

    public PluginJobContext(
        string jobId, string image, IReadOnlyList<string> script,
        IReadOnlyDictionary<string, string> env, string workspacePath, Action<string, bool> output)
    {
        JobId = jobId;
        Image = image;
        Script = script;
        Env = env;
        WorkspacePath = workspacePath;
        _output = output;
    }

    /// <summary>ジョブID（マトリックス展開後の名前を含む）。</summary>
    public string JobId { get; }

    /// <summary>ジョブのコンテナイメージ名。</summary>
    public string Image { get; }

    /// <summary>コンテナ内で実行するコマンド列。</summary>
    public IReadOnlyList<string> Script { get; }

    /// <summary>ジョブの環境変数（グローバルとマージ・展開済み）。</summary>
    public IReadOnlyDictionary<string, string> Env { get; }

    /// <summary>ワークスペース（対象リポジトリ）の絶対パス。</summary>
    public string WorkspacePath { get; }

    /// <summary>標準出力の1行を本体へ通知する。</summary>
    public void WriteLine(string line) => _output(line, false);

    /// <summary>標準エラーの1行を本体へ通知する。</summary>
    public void WriteError(string line) => _output(line, true);
}
