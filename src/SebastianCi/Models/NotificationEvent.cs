namespace SebastianCi.Models;

/// <summary>
/// 通知を送るタイミング（パイプラインのライフサイクル上のイベント）を表す。
/// </summary>
public enum NotificationEvent
{
    /// <summary>パイプライン実行の開始時。</summary>
    Start,

    /// <summary>パイプラインが成功して完了したとき。</summary>
    Success,

    /// <summary>パイプラインが失敗して完了したとき。</summary>
    Failure
}
