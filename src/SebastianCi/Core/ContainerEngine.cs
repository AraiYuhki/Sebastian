using System.ComponentModel;
using System.Diagnostics;

namespace SebastianCi.Core;

/// <summary>
/// コンテナエンジン（Podman / Docker）の差分の吸収と自動検出だけを担当する。
/// </summary>
public sealed class ContainerEngine
{
    /// <summary>Podman。SELinux環境でも安全にマウントできるよう :Z（再ラベル付け）を付与する。</summary>
    public static readonly ContainerEngine Podman = new("podman", ":Z");

    /// <summary>Docker。Docker Desktop等で解釈されないため再ラベル付けサフィックスは付けない。</summary>
    public static readonly ContainerEngine Docker = new("docker", string.Empty);

    private readonly string _mountRelabelSuffix;

    private ContainerEngine(string executableName, string mountRelabelSuffix)
    {
        ExecutableName = executableName;
        _mountRelabelSuffix = mountRelabelSuffix;
    }

    public string ExecutableName { get; }

    /// <summary>--volume に渡す「ホストパス:コンテナパス[:オプション]」形式のマウント指定を組み立てる。</summary>
    public string BuildWorkspaceMountArgument(string hostPath, string containerPath)
        => $"{hostPath}:{containerPath}{_mountRelabelSuffix}";

    /// <summary>エンジン名（podman / docker）から解決する。未対応の名前は null を返す。</summary>
    public static ContainerEngine? FromName(string engineName) => engineName switch
    {
        "podman" => Podman,
        "docker" => Docker,
        _        => null
    };

    /// <summary>利用可能なエンジンを Podman 優先で自動検出する。どちらも無ければ例外をスローする。</summary>
    public static async Task<ContainerEngine> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (await IsAvailableAsync(Podman, cancellationToken)) return Podman;
        if (await IsAvailableAsync(Docker, cancellationToken)) return Docker;

        throw new ContainerExecutionException(
            "podman / docker のいずれも見つかりませんでした。インストールするか、--engine で明示指定してください。");
    }

    private static async Task<bool> IsAvailableAsync(ContainerEngine engine, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = engine.ExecutableName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return false;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // 実行ファイルが存在しない場合に発生する想定内の失敗のため、「利用不可」として扱う
            return false;
        }
    }
}
