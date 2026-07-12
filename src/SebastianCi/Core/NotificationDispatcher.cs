using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 設定された通知先のうち、発生したイベント（on）に一致するものへメッセージを配信する。
/// 通知はベストエフォートで、送信失敗はパイプラインを止めず警告のみに留める。
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyList<NotificationConfig> _configs;
    private readonly IReadOnlyDictionary<string, INotificationChannel> _channelsByType;

    public NotificationDispatcher(
        IReadOnlyList<NotificationConfig> configs, IEnumerable<INotificationChannel> channels)
    {
        _configs = configs;
        _channelsByType = channels.ToDictionary(channel => channel.Type);
    }

    /// <summary>指定イベントを購読している通知先すべてへ、並行してメッセージを送る。</summary>
    public async Task DispatchAsync(
        NotificationEvent triggeredEvent, string message, CancellationToken cancellationToken = default)
    {
        string eventName = ToEventName(triggeredEvent);
        IEnumerable<NotificationConfig> targets =
            _configs.Where(config => config.On.Contains(eventName, StringComparer.OrdinalIgnoreCase));

        await Task.WhenAll(targets.Select(config => SendSafelyAsync(config, message, cancellationToken)));
    }

    private async Task SendSafelyAsync(
        NotificationConfig config, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _channelsByType[config.Type].SendAsync(config, message, cancellationToken);
        }
        catch (Exception exception) when (exception is NotificationException or HttpRequestException)
        {
            ConsoleLogger.WriteWarning($"⚠ {config.Type} への通知に失敗しました: {exception.Message}");
        }
    }

    /// <summary>イベント種別を、on に書かれる小文字の文字列へ変換する。</summary>
    public static string ToEventName(NotificationEvent triggeredEvent) => triggeredEvent switch
    {
        NotificationEvent.Start   => "start",
        NotificationEvent.Success => "success",
        NotificationEvent.Failure => "failure",
        _                         => throw new ArgumentOutOfRangeException(nameof(triggeredEvent))
    };
}
