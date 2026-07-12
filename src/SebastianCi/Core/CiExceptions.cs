namespace SebastianCi.Core;

/// <summary>git コマンドが異常終了したときにスローされる。</summary>
public sealed class GitCommandException(string message) : Exception(message);

/// <summary>コンテナ（podman）実行が異常終了したときにスローされる。</summary>
public sealed class ContainerExecutionException(string message) : Exception(message);

/// <summary>パイプライン定義（.sebastian-ci.yaml）の解析・検証に失敗したときにスローされる。</summary>
public sealed class InvalidPipelineException(string message) : Exception(message);

/// <summary>通知（Slack / ChatWork）の送信に失敗したときにスローされる。</summary>
public sealed class NotificationException(string message) : Exception(message);
