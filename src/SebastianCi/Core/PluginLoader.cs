using System.Reflection;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// plugins で指定された NuGet パッケージ・ローカルアセンブリを読み込み、
/// 拡張点（現状は通知チャンネル INotificationChannel）の実装を取り出す処理だけを担当する。
/// </summary>
public sealed class PluginLoader
{
    private readonly NuGetPluginResolver _nuGetResolver;

    public PluginLoader(string cacheRoot) => _nuGetResolver = new NuGetPluginResolver(cacheRoot);

    /// <summary>
    /// すべてのプラグインを読み込み、見つかった通知チャンネルの実装を返す。
    /// パラメーターなしのコンストラクターを持つ実装だけを対象とする。
    /// </summary>
    public async Task<IReadOnlyList<INotificationChannel>> LoadNotificationChannelsAsync(
        IReadOnlyList<PluginReference> plugins, CancellationToken cancellationToken = default)
    {
        List<INotificationChannel> channels = new();
        foreach (PluginReference plugin in plugins)
        {
            string assemblyPath = await ResolveAssemblyPathAsync(plugin, cancellationToken);
            channels.AddRange(DiscoverChannels(assemblyPath));
        }

        return channels;
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

    private static IEnumerable<INotificationChannel> DiscoverChannels(string assemblyPath)
    {
        PluginLoadContext context = new(assemblyPath);
        Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);

        return assembly.GetTypes()
            .Where(IsInstantiableChannel)
            .Select(Instantiate)
            .ToList();
    }

    private static bool IsInstantiableChannel(Type type)
        => typeof(INotificationChannel).IsAssignableFrom(type)
            && type is { IsAbstract: false, IsInterface: false }
            && type.GetConstructor(Type.EmptyTypes) is not null;

    private static INotificationChannel Instantiate(Type type)
    {
        try
        {
            return (INotificationChannel)Activator.CreateInstance(type)!;
        }
        catch (Exception exception) when (exception is MemberAccessException or TargetInvocationException)
        {
            throw new PluginException($"プラグインの通知チャンネル '{type.FullName}' を生成できませんでした: {exception.Message}");
        }
    }
}
