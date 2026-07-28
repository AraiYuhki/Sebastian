namespace SebastianCi.Core;

/// <summary>
/// approval 指定ジョブの承認をコンソール（標準入力）で受け付ける。
/// 並列実行中に複数の承認が重ならないよう、問い合わせは1件ずつ直列化する。
/// --yes 指定時はすべて自動承認する。
/// </summary>
public sealed class ConsoleApprovalGate : IApprovalGate
{
    private readonly bool _autoApprove;
    private readonly TextReader _input;
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    public ConsoleApprovalGate(bool autoApprove, TextReader? input = null)
    {
        _autoApprove = autoApprove;
        _input = input ?? Console.In;
    }

    public async Task<bool> RequestAsync(string jobId, string message, CancellationToken cancellationToken = default)
    {
        if (_autoApprove)
        {
            ConsoleLogger.WriteInfo($"🔓 ジョブ '{jobId}' の承認ゲートを自動承認しました（--yes）: {message}");
            return true;
        }

        await _promptLock.WaitAsync(cancellationToken);
        try
        {
            return await PromptAsync(jobId, message, cancellationToken);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    private async Task<bool> PromptAsync(string jobId, string message, CancellationToken cancellationToken)
    {
        ConsoleLogger.WriteWarning($"🔐 ジョブ '{jobId}' は承認待ちです: {message}");
        ConsoleLogger.WriteWarning("   実行してよければ y を入力してください（それ以外は中止） > ");

        // Console.In の ReadLine は同期ブロックするため、キャンセル（Ctrl+C）と両立できるよう切り離して待つ
        string? answer = await Task.Run(_input.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);
        if (answer is null)
        {
            ConsoleLogger.WriteError(
                $"❌ ジョブ '{jobId}' の承認を確認できません（標準入力が閉じています）。"
                + "対話できない環境では --yes で自動承認してください。");
            return false;
        }

        return answer.Trim().ToLowerInvariant() is "y" or "yes";
    }
}
