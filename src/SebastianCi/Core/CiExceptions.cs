namespace SebastianCi.Core;

/// <summary>git コマンドが異常終了したときにスローされる。</summary>
public sealed class GitCommandException(string message) : Exception(message);

/// <summary>コンテナ（podman）実行が異常終了したときにスローされる。</summary>
public class ContainerExecutionException(string message) : Exception(message);

/// <summary>
/// エージェントに接続できなかったときにスローされる（ジョブ自体の失敗とは区別する）。
/// プールではこの例外を「別のエージェントを試す」判断に使う。
/// </summary>
public sealed class AgentUnreachableException(string message) : ContainerExecutionException(message);

/// <summary>パイプライン定義（.sebastian-ci.yaml）の解析・検証に失敗したときにスローされる。</summary>
public sealed class InvalidPipelineException(string message) : Exception(message);

/// <summary>通知（Slack / ChatWork）の送信に失敗したときにスローされる。</summary>
public sealed class NotificationException(string message) : Exception(message);

/// <summary>プラグインの解決・読み込みに失敗したときにスローされる。</summary>
public sealed class PluginException(string message) : Exception(message);
