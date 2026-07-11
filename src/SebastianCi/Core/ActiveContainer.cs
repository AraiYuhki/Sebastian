namespace SebastianCi.Core;

/// <summary>
/// 実行中コンテナ1件（どのエンジンで起動した、どの名前のコンテナか）を表す。
/// </summary>
public sealed record ActiveContainer(ContainerEngine Engine, string Name);
