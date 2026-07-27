using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace SebastianCi.Web;

/// <summary>
/// ダッシュボードからの設定ファイルの読み書きと、--validate による検証だけを担当する。
/// </summary>
public sealed class ConfigEditor
{
    private readonly string _repositoryPath;
    private readonly string _configFileName;
    private readonly string _configPath;

    public ConfigEditor(string repositoryPath, string configFileName)
    {
        _repositoryPath = repositoryPath;
        _configFileName = configFileName;
        _configPath = Path.Combine(repositoryPath, configFileName);
    }

    /// <summary>設定ファイルがまだ無いときにエディタへ初期表示する雛形。保存されるまでディスクには書き込まれない。</summary>
    public const string StarterTemplate =
        """
        # sebastian-ci パイプライン定義
        # まだ設定ファイルはありません。この雛形を編集して「保存して検証」を押すと作成されます。
        name: my-pipeline
        image: alpine:3.20          # 各ジョブを動かすコンテナイメージ

        jobs:
          hello:
            script:
              - echo "Hello, sebastian-ci!"
        """;

    public bool Exists => File.Exists(_configPath);

    public string Read() => File.Exists(_configPath) ? File.ReadAllText(_configPath) : "";

    public async Task WriteAsync(string content, CancellationToken cancellationToken = default)
        => await File.WriteAllTextAsync(_configPath, content, cancellationToken);

    /// <summary>現在保存されている設定を --validate で検証し、成否と出力メッセージを返す。</summary>
    public async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        using Process process = new() { StartInfo = BuildValidateStartInfo() };
        StringBuilder output = new();
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new ValidationResult(process.ExitCode == 0, output.ToString().Trim());
    }

    private ProcessStartInfo BuildValidateStartInfo()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        startInfo.ArgumentList.Add(_repositoryPath);
        startInfo.ArgumentList.Add("--validate");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(_configFileName);
        return startInfo;
    }

    private static void AppendLine(StringBuilder output, string? line)
    {
        if (line is not null) output.AppendLine(line);
    }
}
