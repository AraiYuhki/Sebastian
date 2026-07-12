using System.Net.Http.Json;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// Slack の Incoming Webhook へメッセージを送る通知チャンネル。
/// </summary>
public sealed class SlackNotifier : INotificationChannel
{
    public const string TypeName = "slack";

    private readonly HttpClient _httpClient;

    public SlackNotifier(HttpClient httpClient) => _httpClient = httpClient;

    public string Type => TypeName;

    public async Task SendAsync(NotificationConfig config, string message, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response =
            await _httpClient.PostAsJsonAsync(config.Webhook, new { text = message }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationException(
                $"Slack への送信が失敗しました (HTTP {(int)response.StatusCode})。");
        }
    }
}
