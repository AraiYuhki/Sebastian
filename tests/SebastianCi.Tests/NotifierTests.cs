using System.Net;
using System.Text;
using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

/// <summary>
/// ローカルの HttpListener を立て、通知チャンネルが実際に正しい HTTP リクエストを
/// 送っていることを検証する（外部の Slack / ChatWork には接続しない）。
/// </summary>
public sealed class NotifierTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;

    public NotifierTests()
    {
        int port = 8100 + Random.Shared.Next(1500);
        _baseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    public void Dispose() => _listener.Close();

    [Fact]
    public async Task SlackNotifier_PostsJsonTextToWebhook()
    {
        Task<CapturedRequest> capture = CaptureNextAsync();
        using HttpClient client = new();

        await new SlackNotifier(client).SendAsync(new NotificationConfig { Webhook = _baseUrl }, "hello slack");
        CapturedRequest request = await capture;

        Assert.Contains("hello slack", request.Body);
        Assert.Contains("\"text\"", request.Body);
    }

    [Fact]
    public async Task SlackNotifier_ThrowsOnErrorStatus()
    {
        _ = CaptureNextAsync(statusCode: 500);
        using HttpClient client = new();

        await Assert.ThrowsAsync<NotificationException>(
            () => new SlackNotifier(client).SendAsync(new NotificationConfig { Webhook = _baseUrl }, "x"));
    }

    [Fact]
    public async Task ChatWorkNotifier_SendsTokenHeaderAndBody()
    {
        Task<CapturedRequest> capture = CaptureNextAsync();
        using HttpClient client = new();
        ChatWorkNotifier notifier = new(client, _baseUrl.TrimEnd('/'));

        await notifier.SendAsync(new NotificationConfig { Token = "secret-token", Room = "42" }, "hello chatwork");
        CapturedRequest request = await capture;

        Assert.Equal("secret-token", request.TokenHeader);
        Assert.Contains("hello+chatwork", request.Body.Replace("%20", "+"));
        Assert.Contains("/rooms/42/messages", request.Path);
    }

    private Task<CapturedRequest> CaptureNextAsync(int statusCode = 200) => Task.Run(() =>
    {
        HttpListenerContext context = _listener.GetContext();
        using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
        CapturedRequest captured = new(
            reader.ReadToEnd(),
            context.Request.Headers["X-ChatWorkToken"],
            context.Request.Url?.AbsolutePath ?? "");
        context.Response.StatusCode = statusCode;
        context.Response.Close();
        return captured;
    });

    private sealed record CapturedRequest(string Body, string? TokenHeader, string Path);
}
