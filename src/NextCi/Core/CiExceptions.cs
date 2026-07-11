namespace NextCi.Core;

/// <summary>git コマンドが異常終了したときにスローされる。</summary>
public sealed class GitCommandException(string message) : Exception(message);

/// <summary>コンテナ（podman）実行が異常終了したときにスローされる。</summary>
public sealed class ContainerExecutionException(string message) : Exception(message);

/// <summary>パイプライン定義の解析・検証に失敗したときにスローされる。</summary>
public sealed class PipelineValidationException(string message) : Exception(message);
