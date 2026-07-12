namespace SebastianCi.Models;

/// <summary>
/// .sebastian-ci.yaml の plugins 配下1件分の参照。
/// <code>
/// package: NuGet パッケージ ID（NuGet から読み込む場合）
/// version: パッケージのバージョン（package 指定時。省略時は最新）
/// path:    ローカルのアセンブリ（.dll）またはディレクトリのパス
/// </code>
/// package と path のどちらか一方を指定する。
/// </summary>
public sealed class PluginReference
{
    /// <summary>NuGet パッケージ ID。</summary>
    public string Package { get; set; } = string.Empty;

    /// <summary>NuGet パッケージのバージョン（省略時は最新を解決）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>ローカルのアセンブリまたはディレクトリのパス。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>NuGet パッケージ参照かどうか。</summary>
    public bool IsPackage => !string.IsNullOrWhiteSpace(Package);
}
