using System.Reflection;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// plugins で指定された NuGet パッケージ・ローカルアセンブリを読み込み、
/// 拡張点（通知チャンネル・ジョブランナー）の実装を取り出す処理だけを担当する。
/// パラメーターなしのコンストラクターを持つ実装だけを対象とする。
/// </summary>
public sealed class PluginLoader
{
    private readonly NuGetPluginResolver _nuGetResolver;

    public PluginLoader(string cacheRoot) => _nuGetResolver = new NuGetPluginResolver(cacheRoot);

    /// <summary>すべてのプラグインを読み込み、見つかった拡張点の実装をまとめて返す。</summary>
    public async Task<LoadedPlugins> LoadAsync(
        IReadOnlyList<PluginReference> plugins, CancellationToken cancellationToken = default)
    {
        List<INotificationChannel> channels = new();
        List<IPluginJobRunner> jobRunners = new();

        foreach (PluginReference plugin in plugins)
        {
            string assemblyPath = await ResolveAssemblyPathAsync(plugin, cancellationToken);
            Assembly assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
            channels.AddRange(Instantiate<INotificationChannel>(assembly));
            jobRunners.AddRange(Instantiate<IPluginJobRunner>(assembly));
        }

        return new LoadedPlugins(channels, jobRunners);
    }

    private async Task<string> ResolveAssemblyPathAsync(PluginReference plugin, CancellationToken cancellationToken)
    {
        if (plugin.IsPackage)
        {
            return await _nuGetResolver.ResolveAsync(plugin.Package, plugin.Version, cancellationToken);
        }

        string fullPath = Path.GetFullPath(plugin.Path);
        if (Directory.Exists(fullPath)) return FindSingleAssembly(fullPath);
        if (File.Exists(fullPath)) return fullPath;

        throw new PluginException($"プラグインのパスが見つかりません: {plugin.Path}");
    }

    private static string FindSingleAssembly(string directory)
    {
        string[] assemblies = Directory.GetFiles(directory, "*.dll");
        if (assemblies.Length == 0)
        {
            throw new PluginException($"プラグインディレクトリに .dll が見つかりません: {directory}");
        }

        return assemblies[0];
    }

    private static IEnumerable<T> Instantiate<T>(Assembly assembly) where T : class
        => assembly.GetTypes().Where(IsInstantiable<T>).Select(Create<T>).ToList();

    private static bool IsInstantiable<T>(Type type)
        => typeof(T).IsAssignableFrom(type)
            && type is { IsAbstract: false, IsInterface: false }
            && type.GetConstructor(Type.EmptyTypes) is not null;

    private static T Create<T>(Type type) where T : class
    {
        try
        {
            return (T)Activator.CreateInstance(type)!;
        }
        catch (Exception exception) when (exception is MemberAccessException or TargetInvocationException)
        {
            throw new PluginException($"プラグインの型 '{type.FullName}' を生成できませんでした: {exception.Message}");
        }
    }
}

/// <summary>プラグインから読み込んだ拡張点の実装一式。</summary>
public sealed record LoadedPlugins(
    IReadOnlyList<INotificationChannel> Channels, IReadOnlyList<IPluginJobRunner> JobRunners);
