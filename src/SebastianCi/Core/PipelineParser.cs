using SebastianCi.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SebastianCi.Core;

/// <summary>
/// .sebastian-ci.yaml の読み込み・デシリアライズ・バリデーション・正規化だけを担当する。
/// スキーマに存在しないキーは typo 事故を防ぐためエラーとして扱う（厳密モード）。
/// </summary>
public sealed class PipelineParser
{
    public const string DefaultConfigFileName = ".sebastian-ci.yaml";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// 設定ファイルを読み込み、検証・正規化済みのパイプライン定義を返す。
    /// 正規化後は全ジョブの Image が確定し、Env にはグローバル env がマージ済みとなる。
    /// </summary>
    public async Task<PipelineDefinition> ParseAsync(string configFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configFilePath))
        {
            throw new InvalidPipelineException($"設定ファイルが見つかりません: {configFilePath}");
        }

        string yamlContent = await File.ReadAllTextAsync(configFilePath, cancellationToken);
        PipelineDefinition pipeline = DeserializeYaml(yamlContent, configFilePath);
        Validate(pipeline);
        MatrixExpander.Expand(pipeline);
        Normalize(pipeline);
        return pipeline;
    }

    private PipelineDefinition DeserializeYaml(string yamlContent, string configFilePath)
    {
        try
        {
            return _deserializer.Deserialize<PipelineDefinition>(yamlContent)
                ?? throw new InvalidPipelineException($"設定ファイルが空です: {configFilePath}");
        }
        catch (YamlException exception)
        {
            throw new InvalidPipelineException($"YAML の解析に失敗しました ({configFilePath}): {exception.Message}");
        }
    }

    private static void Validate(PipelineDefinition pipeline)
    {
        if (pipeline.Jobs.Count == 0)
        {
            throw new InvalidPipelineException("jobs にジョブが1件も定義されていません。");
        }

        ValidateStages(pipeline.Stages);
        ValidateEnv("グローバル", pipeline.Env);
        ValidateResources(pipeline.Resources);
        ValidateNotifications(pipeline.Notifications);

        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            ValidateJob(jobId, job, pipeline);
        }

        ValidateDependencyCycles(pipeline.Jobs);
    }

    private static void ValidateResources(ResourceThresholds resources)
    {
        if (resources.MinMemoryMb < 0 || resources.MinDiskMb < 0)
        {
            throw new InvalidPipelineException("resources のしきい値に負の値は指定できません。");
        }
    }

    private static void ValidateNotifications(List<NotificationConfig> notifications)
    {
        foreach (NotificationConfig notification in notifications)
        {
            ValidateNotification(notification);
        }
    }

    private static void ValidateNotification(NotificationConfig notification)
    {
        string[] validEvents = ["start", "success", "failure"];
        string? invalidEvent = notification.On.FirstOrDefault(on => !validEvents.Contains(on, StringComparer.OrdinalIgnoreCase));
        if (invalidEvent is not null)
        {
            throw new InvalidPipelineException(
                $"notifications の on に不正な値 '{invalidEvent}' があります（start / success / failure）。");
        }

        ValidateNotificationTarget(notification);
    }

    private static void ValidateNotificationTarget(NotificationConfig notification)
    {
        if (notification.Type == SlackNotifier.TypeName)
        {
            RequireField(notification.Webhook, "slack の webhook");
            return;
        }

        if (notification.Type == ChatWorkNotifier.TypeName)
        {
            RequireField(notification.Token, "chatwork の token");
            RequireField(notification.Room, "chatwork の room");
            return;
        }

        throw new InvalidPipelineException(
            $"notifications の type '{notification.Type}' は未対応です（slack / chatwork）。");
    }

    private static void RequireField(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidPipelineException($"notifications で {label} が指定されていません。");
        }
    }

    private static void ValidateStages(List<string> stages)
    {
        if (stages.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException("stages に空のステージ名が含まれています。");
        }

        if (stages.Distinct().Count() != stages.Count)
        {
            throw new InvalidPipelineException("stages に重複したステージ名が含まれています。");
        }
    }

    private static void ValidateEnv(string ownerLabel, Dictionary<string, string> env)
    {
        if (env.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"{ownerLabel} の env に空のキーが含まれています。");
        }
    }

    private static void ValidateJob(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        ValidateJobImage(jobId, job, pipeline);

        if (job.Script.Count == 0)
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' に script が1件も指定されていません。");
        }

        if (job.Script.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の script に空のコマンドが含まれています。");
        }

        if (job.Changes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の changes に空のパターンが含まれています。");
        }

        if (job.Timeout < 0)
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の timeout に負の値は指定できません。");
        }

        if (job.Retry < 0)
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の retry に負の値は指定できません。");
        }

        ValidateEnv($"ジョブ '{jobId}'", job.Env);
        ValidateCache(jobId, job);
        ValidateMatrix(jobId, job);
        ValidateArtifacts(jobId, job);
        ValidateJobNeeds(jobId, job, pipeline.Jobs);
        ValidateJobStage(jobId, job, pipeline);
    }

    private static void ValidateCache(string jobId, JobDefinition job)
    {
        if (job.Cache.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の cache に空のパスが含まれています。");
        }

        string? relativePath = job.Cache.FirstOrDefault(path => !Path.IsPathRooted(path));
        if (relativePath is not null)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の cache '{relativePath}' は、コンテナ内の絶対パスで指定してください。");
        }
    }

    private static void ValidateJobImage(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        bool hasMatrixImage = job.Matrix.ContainsKey(MatrixExpander.ImageKey);
        if (hasMatrixImage && !string.IsNullOrWhiteSpace(job.Image))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' では matrix の image とジョブの image を併用できません。");
        }

        if (hasMatrixImage) return;

        if (string.IsNullOrWhiteSpace(job.Image) && string.IsNullOrWhiteSpace(pipeline.Image))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' に image が指定されておらず、グローバル image も未定義です。");
        }
    }

    private static void ValidateMatrix(string jobId, JobDefinition job)
    {
        if (job.Matrix.Count == 0) return;

        if (job.Matrix.Keys.Any(string.IsNullOrWhiteSpace) || job.Matrix.Keys.Any(key => key.Contains('=')))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の matrix に不正な変数名が含まれています。");
        }

        if (job.Matrix.Values.Any(values => values.Count == 0 || values.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の matrix に空の値リストまたは空の値が含まれています。");
        }

        int combinationCount = job.Matrix.Values.Aggregate(1, (total, values) => total * values.Count);
        if (combinationCount > MatrixExpander.MaxCombinationCount)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の matrix の組み合わせ数 {combinationCount} が上限 {MatrixExpander.MaxCombinationCount} を超えています。");
        }
    }

    private static void ValidateArtifacts(string jobId, JobDefinition job)
    {
        if (job.Artifacts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の artifacts に空のパスが含まれています。");
        }

        string? unsafeArtifactPath = job.Artifacts.FirstOrDefault(IsUnsafeArtifactPath);
        if (unsafeArtifactPath is not null)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の artifacts '{unsafeArtifactPath}' は、リポジトリ内を指す相対パスで指定してください。");
        }
    }

    private static bool IsUnsafeArtifactPath(string artifactPath)
        => Path.IsPathRooted(artifactPath) || artifactPath.Split('/', '\\').Contains("..");

    private static void ValidateJobNeeds(string jobId, JobDefinition job, Dictionary<string, JobDefinition> allJobs)
    {
        if (job.Needs.Contains(jobId))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' が needs で自分自身に依存しています。");
        }

        if (job.Needs.Distinct().Count() != job.Needs.Count)
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の needs に重複したジョブ名が含まれています。");
        }

        string? unknownDependency = job.Needs.FirstOrDefault(needId => !allJobs.ContainsKey(needId));
        if (unknownDependency is not null)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の needs に未定義のジョブ '{unknownDependency}' が指定されています。");
        }
    }

    private static void ValidateJobStage(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        bool hasStages = pipeline.Stages.Count > 0;
        if (!hasStages && !string.IsNullOrWhiteSpace(job.Stage))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' に stage '{job.Stage}' が指定されていますが、stages が定義されていません。");
        }

        if (!hasStages) return;

        if (string.IsNullOrWhiteSpace(job.Stage))
        {
            throw new InvalidPipelineException($"stages 定義時は必須ですが、ジョブ '{jobId}' に stage がありません。");
        }

        if (!pipeline.Stages.Contains(job.Stage))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の stage '{job.Stage}' は stages に定義されていません。");
        }

        ValidateNeedsStageOrder(jobId, job, pipeline);
    }

    private static void ValidateNeedsStageOrder(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        int jobStageIndex = pipeline.Stages.IndexOf(job.Stage);
        foreach (string needId in job.Needs)
        {
            int needStageIndex = pipeline.Stages.IndexOf(pipeline.Jobs[needId].Stage);
            if (needStageIndex > jobStageIndex)
            {
                throw new InvalidPipelineException(
                    $"ジョブ '{jobId}' ({job.Stage}) が後のステージのジョブ '{needId}' に依存しています。");
            }
        }
    }

    private static void ValidateDependencyCycles(Dictionary<string, JobDefinition> jobs)
    {
        Dictionary<string, IReadOnlyList<string>> explicitNeeds =
            jobs.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Needs);
        DependencyGraph.SortTopologically(explicitNeeds);
    }

    private static void Normalize(PipelineDefinition pipeline)
    {
        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            NormalizeJob(jobId, job, pipeline);
        }

        foreach (NotificationConfig notification in pipeline.Notifications)
        {
            NormalizeNotification(notification);
        }
    }

    /// <summary>通知先の秘密情報（webhook / token / room）に含まれる $NAME をホスト環境変数で展開する。</summary>
    private static void NormalizeNotification(NotificationConfig notification)
    {
        notification.Webhook = EnvironmentVariableExpander.Expand(notification.Webhook, "notifications の webhook");
        notification.Token = EnvironmentVariableExpander.Expand(notification.Token, "notifications の token");
        notification.Room = EnvironmentVariableExpander.Expand(notification.Room, "notifications の room");
    }

    private static void NormalizeJob(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        if (string.IsNullOrWhiteSpace(job.Image))
        {
            job.Image = pipeline.Image;
        }

        job.Env = ExpandEnvValues(jobId, MergeEnv(pipeline.Env, job.Env));
    }

    private static Dictionary<string, string> ExpandEnvValues(string jobId, Dictionary<string, string> env)
    {
        Dictionary<string, string> expandedEnv = new(env.Count);
        foreach ((string key, string value) in env)
        {
            expandedEnv[key] = EnvironmentVariableExpander.Expand(value, $"ジョブ '{jobId}' の env '{key}'");
        }

        return expandedEnv;
    }

    private static Dictionary<string, string> MergeEnv(
        Dictionary<string, string> globalEnv, Dictionary<string, string> jobEnv)
    {
        Dictionary<string, string> mergedEnv = new(globalEnv);
        foreach ((string key, string value) in jobEnv)
        {
            mergedEnv[key] = value;
        }

        return mergedEnv;
    }
}
