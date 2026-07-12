using System.Reflection;
using System.Runtime.Loader;

namespace SebastianCi.Core;

/// <summary>
/// プラグインのアセンブリを読み込む隔離コンテキスト。
/// ホスト本体（sebastian-ci）やフレームワークのアセンブリは既定コンテキストと共有し、
/// プラグイン固有の依存だけを隔離して読み込む（型の同一性を保つため）。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 既定コンテキストに既にあるアセンブリ（ホスト本体・フレームワーク）は共有する。
        if (IsSharedWithHost(assemblyName)) return null;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    private static bool IsSharedWithHost(AssemblyName assemblyName)
        => Default.Assemblies.Any(loaded => loaded.GetName().Name == assemblyName.Name);
}
