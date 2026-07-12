namespace SebastianCi.Models;

/// <summary>
/// .sebastian-ci.yaml の notifications 配下1件分のスキーマを表す。
/// <code>
/// type:    通知先の種別（slack / chatwork）
/// on:      送信するタイミング（start / success / failure・複数指定可）
/// webhook: Slack の Incoming Webhook URL（type=slack のとき必須）
/// token:   ChatWork の API トークン（type=chatwork のとき必須）
/// room:    ChatWork のルームID（type=chatwork のとき必須）
/// </code>
/// webhook / token / room の値では $NAME 形式でホスト環境変数を参照できる（秘密情報向け）。
/// </summary>
public sealed class NotificationConfig
{
    /// <summary>通知先の種別（slack / chatwork）。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>送信するタイミングを表す文字列の配列（start / success / failure）。</summary>
    public List<string> On { get; set; } = new();

    /// <summary>Slack の Incoming Webhook URL。</summary>
    public string Webhook { get; set; } = string.Empty;

    /// <summary>ChatWork の API トークン。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>ChatWork のルームID。</summary>
    public string Room { get; set; } = string.Empty;
}
