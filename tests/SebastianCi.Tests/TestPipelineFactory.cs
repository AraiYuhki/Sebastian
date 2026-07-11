using SebastianCi.Models;

namespace SebastianCi.Tests;

/// <summary>
/// テスト用のパイプライン・ジョブを簡潔に組み立てるためのヘルパー。
/// </summary>
internal static class TestPipelineFactory
{
    public static JobDefinition Job(
        string image = "alpine",
        List<string>? needs = null,
        List<string>? script = null,
        string stage = "")
        => new()
        {
            Image = image,
            Stage = stage,
            Needs = needs ?? new(),
            Script = script ?? ["echo hello"]
        };

    public static PipelineDefinition Pipeline(params (string Id, JobDefinition Job)[] jobs)
        => new()
        {
            Jobs = jobs.ToDictionary(entry => entry.Id, entry => entry.Job)
        };
}
