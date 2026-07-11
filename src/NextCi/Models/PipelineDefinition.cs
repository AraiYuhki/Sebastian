namespace NextCi.Models;

/// <summary>
/// .next-ci.yaml 全体（パイプライン名とジョブ一覧）を保持する。
/// </summary>
public sealed class PipelineDefinition
{
    public string Name { get; set; } = "pipeline";

    /// <summary>ジョブIDをキーとしたジョブ定義の一覧。</summary>
    public Dictionary<string, JobDefinition> Jobs { get; set; } = new();
}
