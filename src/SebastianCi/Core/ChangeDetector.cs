using Microsoft.Extensions.FileSystemGlobbing;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 前回成功コミットとの差分ファイルに基づく、changes 指定ジョブの実行要否判定だけを担当する。
/// </summary>
public sealed class ChangeDetector
{
    /// <summary>差分ファイル一覧。null は「差分不明（初回実行や履歴なし）」を意味し、全ジョブが実行される。</summary>
    private readonly IReadOnlyList<string>? _changedFilePaths;

    public ChangeDetector(IReadOnlyList<string>? changedFilePaths) => _changedFilePaths = changedFilePaths;

    /// <summary>changes 未指定・差分不明・パターン一致のいずれかであれば true（実行する）を返す。</summary>
    public bool ShouldRun(JobDefinition job)
    {
        if (job.Changes.Count == 0) return true;
        if (_changedFilePaths is null) return true;

        Matcher matcher = new();
        matcher.AddIncludePatterns(job.Changes);
        return matcher.Match(_changedFilePaths).HasMatches;
    }
}
