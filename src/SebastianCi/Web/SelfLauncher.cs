using System.Diagnostics;
using System.Reflection;

namespace SebastianCi.Web;

/// <summary>sebastian-ci 自身を子プロセスとして起動するための ProcessStartInfo を組み立てる。</summary>
public static class SelfLauncher
{
    public static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        string fileName = Environment.ProcessPath ?? "dotnet";
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // `dotnet sebastian-ci.dll ...` 形式で動いているときだけ、dll パスを先頭に付ける。
        if (Path.GetFileNameWithoutExtension(fileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
