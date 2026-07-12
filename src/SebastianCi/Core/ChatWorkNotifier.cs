using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ChatWork の API（rooms/{room_id}/messages）へメッセージを送る通知チャンネル。
/// </summary>
public sealed class ChatWorkNotifier : INotificationChannel
{
    public const string TypeName = "chatwork";

    private const string DefaultApiBaseUrl = "https://api.chatwork.com/v2";
    private const string TokenHeaderName = "X-ChatWorkToken";

    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public ChatWorkNotifier(HttpClient httpClient, string? apiBaseUrl = null)
    {
        _httpClient = httpClient;
        _apiBaseUrl = apiBaseUrl ?? DefaultApiBaseUrl;
    }

    public string Type => TypeName;

    public async Task SendAsync(NotificationConfig config, string message, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{_apiBaseUrl}/rooms/{config.Room}/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = message })
        };
        request.Headers.Add(TokenHeaderName, config.Token);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationException(
                $"ChatWork への送信が失敗しました (HTTP {(int)response.StatusCode})。");
        }
    }
}
