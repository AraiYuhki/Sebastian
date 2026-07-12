using System.Diagnostics;

namespace SebastianCi.Core;

/// <summary>
/// NuGet パッケージを、それを参照する一時プロジェクトのビルドを通じてローカルの
/// アセンブリ群へ解決する処理だけを担当する。解決結果は再利用のためキャッシュする。
/// </summary>
public sealed class NuGetPluginResolver
{
    private readonly string _cacheRoot;

    public NuGetPluginResolver(string cacheRoot) => _cacheRoot = cacheRoot;

    /// <summary>
    /// 指定パッケージを復元・ビルドし、パッケージ本体の DLL パスを返す。
    /// DLL は依存も含めて同じディレクトリに配置される。
    /// </summary>
    public async Task<string> ResolveAsync(
        string packageId, string version, CancellationToken cancellationToken = default)
    {
        string outputDirectory = Path.Combine(_cacheRoot, SanitizeKey(packageId, version));
        string mainAssembly = Path.Combine(outputDirectory, $"{packageId}.dll");
        if (File.Exists(mainAssembly)) return mainAssembly;

        await BuildTemporaryProjectAsync(packageId, version, outputDirectory, cancellationToken);
        if (!File.Exists(mainAssembly))
        {
            throw new PluginException(
                $"NuGet パッケージ '{packageId}' から '{packageId}.dll' が見つかりませんでした。");
        }

        return mainAssembly;
    }

    private async Task BuildTemporaryProjectAsync(
        string packageId, string version, string outputDirectory, CancellationToken cancellationToken)
    {
        string projectDirectory = Path.Combine(_cacheRoot, "_build", SanitizeKey(packageId, version));
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "plugin.csproj"), BuildProjectXml(packageId, version), cancellationToken);

        int exitCode = await RunDotnetBuildAsync(projectDirectory, outputDirectory, cancellationToken);
        if (exitCode != 0)
        {
            throw new PluginException($"NuGet パッケージ '{packageId}' の取得に失敗しました（dotnet build 失敗）。");
        }
    }

    private static string BuildProjectXml(string packageId, string version)
    {
        string versionAttribute = string.IsNullOrWhiteSpace(version) ? "" : $" Version=\"{version}\"";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{packageId}"{versionAttribute} />
              </ItemGroup>
            </Project>
            """;
    }

    private static async Task<int> RunDotnetBuildAsync(
        string projectDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "build", "-c", "Release", "-o", outputDirectory })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new PluginException("dotnet プロセスを起動できませんでした。");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static string SanitizeKey(string packageId, string version)
        => PathSanitizer.ToFileSystemName($"{packageId}@{(string.IsNullOrWhiteSpace(version) ? "latest" : version)}");
}
