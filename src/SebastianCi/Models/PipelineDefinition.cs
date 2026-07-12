namespace SebastianCi.Models;

/// <summary>
/// .sebastian-ci.yaml 全体のスキーマを表す。
/// <code>
/// name:   パイプライン名（任意）
/// image:  グローバル既定のPodmanイメージ名（ジョブ側で省略された場合に使用）
/// env:    全ジョブへ -e で渡す環境変数マップ（任意）
/// stages: 実行順序を制御するステージ名の配列（任意・定義順 = 実行順）
/// jobs:   ジョブ識別子をキーとするジョブ定義マップ（必須）
/// notifications: Slack / ChatWork への通知設定（任意）
/// resources: リソース警告のしきい値（任意）
/// </code>
/// </summary>
public sealed class PipelineDefinition
{
    public string Name { get; set; } = "pipeline";

    /// <summary>グローバル既定のコンテナイメージ。ジョブ固有の image が優先される。</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>全ジョブ共通の環境変数。ジョブ側の同名キーが優先される。</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>ステージ名の配列。前のステージの全ジョブが完了してから次のステージが始まる。</summary>
    public List<string> Stages { get; set; } = new();

    /// <summary>ジョブIDをキーとしたジョブ定義の一覧。</summary>
    public Dictionary<string, JobDefinition> Jobs { get; set; } = new();

    /// <summary>Slack / ChatWork への通知設定。空なら通知しない。</summary>
    public List<NotificationConfig> Notifications { get; set; } = new();

    /// <summary>リソース警告のしきい値。</summary>
    public ResourceThresholds Resources { get; set; } = new();

    /// <summary>エージェントプール。remote 指定ジョブの割り当て先候補。</summary>
    public List<AgentEndpoint> Agents { get; set; } = new();
}
