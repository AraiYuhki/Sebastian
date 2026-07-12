using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public class NotificationDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_SendsOnlyToChannelsSubscribedToTheEvent()
    {
        RecordingChannel channel = new();
        NotificationConfig failureOnly = new() { Type = "test", On = ["failure"] };
        NotificationDispatcher dispatcher = new([failureOnly], [channel]);

        await dispatcher.DispatchAsync(NotificationEvent.Success, "s");
        Assert.Empty(channel.SentMessages);

        await dispatcher.DispatchAsync(NotificationEvent.Failure, "f");
        Assert.Equal(["f"], channel.SentMessages);
    }

    [Fact]
    public async Task DispatchAsync_SwallowsSendFailuresWithoutThrowing()
    {
        NotificationConfig config = new() { Type = "test", On = ["start"] };
        NotificationDispatcher dispatcher = new([config], [new ThrowingChannel()]);

        // 送信失敗（NotificationException）でも例外を伝播せず握って警告のみに留める
        await dispatcher.DispatchAsync(NotificationEvent.Start, "hi");
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<string> SentMessages { get; } = new();
        public string Type => "test";

        public Task SendAsync(NotificationConfig config, string message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public string Type => "test";

        public Task SendAsync(NotificationConfig config, string message, CancellationToken cancellationToken = default)
            => throw new NotificationException("boom");
    }
}
