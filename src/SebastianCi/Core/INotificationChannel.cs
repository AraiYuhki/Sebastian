using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 単一の通知先（Slack / ChatWork など）へメッセージを送る責務を表す。
/// </summary>
public interface INotificationChannel
{
    /// <summary>この実装が扱う通知種別（notifications の type と一致させる）。</summary>
    string Type { get; }

    /// <summary>設定に従ってメッセージを送信する。</summary>
    Task SendAsync(NotificationConfig config, string message, CancellationToken cancellationToken = default);
}
