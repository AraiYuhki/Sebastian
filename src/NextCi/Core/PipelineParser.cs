using NextCi.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NextCi.Core;

/// <summary>
/// .next-ci.yaml の読み込み・デシリアライズ・バリデーションだけを担当する。
/// </summary>
public sealed class PipelineParser
{
    public const string DefaultConfigFileName = ".next-ci.yaml";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>設定ファイルを読み込み、検証済みのパイプライン定義を返す。</summary>
    public async Task<PipelineDefinition> ParseAsync(string configFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configFilePath))
        {
            throw new PipelineValidationException($"設定ファイルが見つかりません: {configFilePath}");
        }

        string yamlContent = await File.ReadAllTextAsync(configFilePath, cancellationToken);
        PipelineDefinition pipeline = DeserializeYaml(yamlContent, configFilePath);
        Validate(pipeline);
        return pipeline;
    }

    private PipelineDefinition DeserializeYaml(string yamlContent, string configFilePath)
    {
        try
        {
            return _deserializer.Deserialize<PipelineDefinition>(yamlContent)
                ?? throw new PipelineValidationException($"設定ファイルが空です: {configFilePath}");
        }
        catch (YamlException exception)
        {
            throw new PipelineValidationException($"YAML の解析に失敗しました ({configFilePath}): {exception.Message}");
        }
    }

    private static void Validate(PipelineDefinition pipeline)
    {
        if (pipeline.Jobs.Count == 0)
        {
            throw new PipelineValidationException("jobs にジョブが1件も定義されていません。");
        }

        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            ValidateJob(jobId, job, pipeline.Jobs);
        }
    }

    private static void ValidateJob(string jobId, JobDefinition job, Dictionary<string, JobDefinition> allJobs)
    {
        if (string.IsNullOrWhiteSpace(job.Image))
        {
            throw new PipelineValidationException($"ジョブ '{jobId}' に image が指定されていません。");
        }

        if (job.Commands.Count == 0)
        {
            throw new PipelineValidationException($"ジョブ '{jobId}' に commands が1件も指定されていません。");
        }

        string? unknownDependency = job.Needs.FirstOrDefault(needId => !allJobs.ContainsKey(needId));
        if (unknownDependency is not null)
        {
            throw new PipelineValidationException(
                $"ジョブ '{jobId}' の needs に未定義のジョブ '{unknownDependency}' が指定されています。");
        }
    }
}
