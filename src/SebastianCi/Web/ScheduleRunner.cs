using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>
/// serve プロセスが起動している間だけ、cron式に基づいてパイプライン実行をトリガーする。
/// 実行の実体は RunManager が sebastian-ci 本体を子プロセスとして起動する形なので、
/// コマンドラインや手動実行と完全に同じ（git連動・コンテナ・通知・履歴）動作になる。
/// serve を止めれば止まる（他の機能と同様、常駐サーバーを必須にはしない）。
/// </summary>
public sealed class ScheduleRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly ConfigEditor _configEditor;
    private readonly RunManager _runManager;
    private readonly Dictionary<string, DateTime> _lastTriggeredMinute = new();

    public ScheduleRunner(ConfigEditor configEditor, RunManager runManager)
    {
        _configEditor = configEditor;
        _runManager = runManager;
    }

    /// <summary>停止要求があるまでポーリングを続ける。例外は握りつぶさずログに出し、ループ自体は継続する。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception exception)
            {
                ConsoleLogger.WriteWarning($"⚠ スケジュール実行の確認中にエラーが発生しました: {exception.Message}");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick()
    {
        if (!_configEditor.Exists) return;

        List<ScheduleSummary> schedules;
        try
        {
            schedules = ScheduleConfigEditor.Read(_configEditor.Read());
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return;
        }

        DateTime now = DateTime.Now;
        DateTime currentMinute = new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        foreach (ScheduleSummary schedule in schedules)
        {
            TryTrigger(schedule, now, currentMinute);
        }
    }

    private void TryTrigger(ScheduleSummary schedule, DateTime now, DateTime currentMinute)
    {
        if (!schedule.Enabled) return;
        if (!CronExpression.TryParse(schedule.Cron, out CronExpression? cron, out _) || cron is null) return;
        if (!cron.Matches(now)) return;
        if (_lastTriggeredMinute.TryGetValue(schedule.Id, out DateTime last) && last == currentMinute) return;

        _lastTriggeredMinute[schedule.Id] = currentMinute;
        bool started = _runManager.TryStart(schedule.Rebuild, schedule.Job, schedule.Params, schedule.Yes);
        ConsoleLogger.WriteInfo(started
            ? $"⏰ スケジュール「{schedule.Id}」により実行を開始しました。"
            : $"⚠ スケジュール「{schedule.Id}」の実行時刻でしたが、既に別の実行が進行中のためスキップしました。");
    }
}
