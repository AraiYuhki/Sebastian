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

    private readonly IReadOnlyDictionary<string, string> _parameterOverrides;

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <param name="parameterOverrides">--param で与えられた実行時パラメーターの上書き値（パラメーター名→値）。</param>
    public PipelineParser(IReadOnlyDictionary<string, string>? parameterOverrides = null)
        => _parameterOverrides = parameterOverrides ?? new Dictionary<string, string>();

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
        ValidateParameterOverrides(pipeline);
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
        ValidateParams(pipeline.Params);
        ValidateEnv("グローバル", pipeline.Env);
        ValidateResources(pipeline.Resources);
        ValidateNotifications(pipeline.Notifications);
        ValidateAgents(pipeline.Agents);
        ValidatePlugins(pipeline.Plugins);
        ValidateSchedules(pipeline.Schedules, pipeline.Jobs);

        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            ValidateJob(jobId, job, pipeline);
        }

        ValidateDependencyCycles(pipeline.Jobs);
    }

    private static void ValidateAgents(List<AgentEndpoint> agents)
    {
        if (agents.Any(agent => string.IsNullOrWhiteSpace(agent.Url)))
        {
            throw new InvalidPipelineException("agents に url の無いエントリがあります。");
        }

        if (agents.Any(agent => agent.Labels.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidPipelineException("agents の labels に空のラベルが含まれています。");
        }
    }

    private static void ValidateSchedules(
        Dictionary<string, Models.ScheduleDefinition> schedules, Dictionary<string, JobDefinition> jobs)
    {
        foreach ((string scheduleId, Models.ScheduleDefinition schedule) in schedules)
        {
            if (!CronExpression.TryParse(schedule.Cron, out _, out string? error))
            {
                throw new InvalidPipelineException($"schedules.{scheduleId}: {error}");
            }

            if (schedule.Job is { Length: > 0 } jobId && !jobs.ContainsKey(jobId))
            {
                throw new InvalidPipelineException($"schedules.{scheduleId}: ジョブ「{jobId}」は jobs にありません。");
            }
        }
    }

    private static void ValidatePlugins(List<PluginReference> plugins)
    {
        foreach (PluginReference plugin in plugins)
        {
            bool hasPackage = !string.IsNullOrWhiteSpace(plugin.Package);
            bool hasPath = !string.IsNullOrWhiteSpace(plugin.Path);
            if (hasPackage == hasPath)
            {
                throw new InvalidPipelineException("plugins の各エントリは package か path のどちらか一方を指定してください。");
            }
        }
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
        if (string.IsNullOrWhiteSpace(notification.Type))
        {
            throw new InvalidPipelineException("notifications に type の無いエントリがあります。");
        }

        if (notification.Type == SlackNotifier.TypeName)
        {
            RequireField(notification.Webhook, "slack の webhook");
            return;
        }

        if (notification.Type == ChatWorkNotifier.TypeName)
        {
            RequireField(notification.Token, "chatwork の token");
            RequireField(notification.Room, "chatwork の room");
        }

        // 上記以外の type はプラグイン提供の可能性があるため、ここでは拒否しない。
        // 実行時に対応するチャンネルが見つからなければエラーになる。
    }

    private static void RequireField(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidPipelineException($"notifications で {label} が指定されていません。");
        }
    }

    private static void ValidateParams(Dictionary<string, ParamDefinition> parameters)
    {
        foreach ((string name, ParamDefinition parameter) in parameters)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidPipelineException("params に空のパラメーター名が含まれています。");
            }

            if (parameter.Choices.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidPipelineException($"パラメーター '{name}' の choices に空の値が含まれています。");
            }

            bool defaultOutsideChoices = parameter is { Default: not null, Choices.Count: > 0 }
                && !parameter.Choices.Contains(parameter.Default);
            if (defaultOutsideChoices)
            {
                throw new InvalidPipelineException(
                    $"パラメーター '{name}' の default '{parameter.Default}' は choices に含まれていません。");
            }
        }
    }

    /// <summary>--param の上書き値が定義済みパラメーターを指し、choices の制約を満たすかを確認する。</summary>
    private void ValidateParameterOverrides(PipelineDefinition pipeline)
    {
        foreach ((string name, string value) in _parameterOverrides)
        {
            if (!pipeline.Params.TryGetValue(name, out ParamDefinition? parameter))
            {
                throw new InvalidPipelineException(
                    $"--param で指定された '{name}' は params に定義されていません。");
            }

            if (parameter.Choices.Count > 0 && !parameter.Choices.Contains(value))
            {
                throw new InvalidPipelineException(
                    $"パラメーター '{name}' の値 '{value}' は choices（{string.Join(" / ", parameter.Choices)}）に含まれていません。");
            }
        }
    }

    /// <summary>各パラメーターの実効値（--param の上書き → default の順）を決める。必須パラメーターの欠落はエラー。</summary>
    private Dictionary<string, string> ResolveParams(Dictionary<string, ParamDefinition> parameters)
    {
        Dictionary<string, string> resolved = new(parameters.Count);
        foreach ((string name, ParamDefinition parameter) in parameters)
        {
            if (_parameterOverrides.TryGetValue(name, out string? overrideValue))
            {
                resolved[name] = overrideValue;
                continue;
            }

            resolved[name] = parameter.Default ?? throw new InvalidPipelineException(
                BuildMissingParamMessage(name, parameter));
        }

        return resolved;
    }

    private static string BuildMissingParamMessage(string name, ParamDefinition parameter)
    {
        string description = string.IsNullOrWhiteSpace(parameter.Description) ? "" : $"（{parameter.Description}）";
        return $"パラメーター '{name}'{description} には default が無いため、--param {name}=<値> の指定が必要です。";
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
        ValidateJobShell(jobId, job);
        ValidateJobImage(jobId, job, pipeline);

        if (job.Script.Count == 0)
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' に script が1件も指定されていません。");
        }

        if (job.Script.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の script に空のコマンドが含まれています。");
        }

        if (job.AfterScript.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の afterScript に空のコマンドが含まれています。");
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
        ValidateReports(jobId, job);
        ValidateJobRemote(jobId, job, pipeline);
        ValidateJobNeeds(jobId, job, pipeline.Jobs);
        ValidateJobStage(jobId, job, pipeline);
    }

    /// <summary>shell（ホスト直接実行）ジョブは、コンテナ前提の設定（image / cache / runner）と併用できない。</summary>
    private static void ValidateJobShell(string jobId, JobDefinition job)
    {
        if (!job.Shell) return;

        if (!string.IsNullOrWhiteSpace(job.Image) || job.Matrix.ContainsKey(MatrixExpander.ImageKey))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' は shell 指定のため image は指定できません（コンテナを使いません）。");
        }

        if (job.Cache.Count > 0)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' は shell 指定のため cache は使えません（コンテナ内パスをマウントする機能のため）。");
        }

        if (!string.IsNullOrWhiteSpace(job.Runner))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' で shell と runner は併用できません。");
        }
    }

    private static void ValidateJobRemote(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        if (job.Remote && !string.IsNullOrWhiteSpace(job.Agent))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' で agent と remote は併用できません。");
        }

        if (job.Remote && pipeline.Agents.Count == 0)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' は remote 指定ですが、agents（エージェントプール）が定義されていません。");
        }

        bool hasRunner = !string.IsNullOrWhiteSpace(job.Runner);
        if (hasRunner && (job.Remote || !string.IsNullOrWhiteSpace(job.Agent)))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' で runner と agent / remote は併用できません。");
        }

        ValidateJobLabels(jobId, job, pipeline);
    }

    /// <summary>labels は remote（プール割り当て）の絞り込み条件。満たせるエージェントの存在まで確認する。</summary>
    private static void ValidateJobLabels(string jobId, JobDefinition job, PipelineDefinition pipeline)
    {
        if (job.Labels.Count == 0) return;

        if (job.Labels.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の labels に空のラベルが含まれています。");
        }

        if (!job.Remote)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の labels は remote: true と組み合わせて使います（agent 直接指定では不要です）。");
        }

        if (!pipeline.Agents.Any(agent => agent.Satisfies(job.Labels)))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の labels（{string.Join(", ", job.Labels)}）をすべて満たすエージェントが agents にありません。");
        }
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
        // shell ジョブはコンテナを使わないため image 不要（併用は ValidateJobShell で拒否済み）
        if (job.Shell) return;

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

    private static void ValidateReports(string jobId, JobDefinition job)
    {
        if (job.Reports.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidPipelineException($"ジョブ '{jobId}' の reports に空のパターンが含まれています。");
        }

        string? unsafeReportPattern = job.Reports.FirstOrDefault(IsUnsafeArtifactPath);
        if (unsafeReportPattern is not null)
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の reports '{unsafeReportPattern}' は、リポジトリ内を指す相対パターンで指定してください。");
        }
    }

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

    private void Normalize(PipelineDefinition pipeline)
    {
        Dictionary<string, string> resolvedParams = ResolveParams(pipeline.Params);
        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            NormalizeJob(jobId, job, pipeline, resolvedParams);
        }

        foreach (NotificationConfig notification in pipeline.Notifications)
        {
            NormalizeNotification(notification);
        }

        foreach (AgentEndpoint agent in pipeline.Agents)
        {
            agent.Token = EnvironmentVariableExpander.Expand(agent.Token, $"agents の token ({agent.Url})");
        }
    }

    /// <summary>通知先の秘密情報（webhook / token / room）に含まれる $NAME をホスト環境変数で展開する。</summary>
    private static void NormalizeNotification(NotificationConfig notification)
    {
        notification.Webhook = EnvironmentVariableExpander.Expand(notification.Webhook, "notifications の webhook");
        notification.Token = EnvironmentVariableExpander.Expand(notification.Token, "notifications の token");
        notification.Room = EnvironmentVariableExpander.Expand(notification.Room, "notifications の room");
    }

    private static void NormalizeJob(
        string jobId, JobDefinition job, PipelineDefinition pipeline, Dictionary<string, string> resolvedParams)
    {
        // shell ジョブはコンテナを使わないため、グローバル image は継承させない
        if (!job.Shell && string.IsNullOrWhiteSpace(job.Image))
        {
            job.Image = pipeline.Image;
        }

        Dictionary<string, string> jobEnv = job.Env;
        Dictionary<string, string> mergedEnv = ExpandEnvValues(jobId, MergeEnv(pipeline.Env, jobEnv));
        ApplyResolvedParams(mergedEnv, jobEnv, resolvedParams);
        job.Env = mergedEnv;
        job.AgentToken = EnvironmentVariableExpander.Expand(job.AgentToken, $"ジョブ '{jobId}' の agentToken");
    }

    /// <summary>
    /// 解決済みパラメーターを環境変数として重ねる。優先順位は グローバル env &lt; params &lt; ジョブ env（matrix 含む）。
    /// パラメーター値は $NAME 展開の対象にしない（--param で渡された値をそのまま使う）。
    /// </summary>
    private static void ApplyResolvedParams(
        Dictionary<string, string> mergedEnv, Dictionary<string, string> jobEnv,
        Dictionary<string, string> resolvedParams)
    {
        foreach ((string name, string value) in resolvedParams)
        {
            if (!jobEnv.ContainsKey(name)) mergedEnv[name] = value;
        }
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
