using SebastianCi.Core;

namespace SebastianCi.Web;

public enum RepositoryState { Ready, Cloning, Error }

/// <summary>ハブが管理する1リポジトリの実行時の状態（登録情報 + API + スケジュール実行のタスク）。</summary>
public sealed class RepositoryHandle
{
    public required RepositoryEntry Entry { get; set; }
    public RepositoryState State { get; set; }
    public string? Error { get; set; }
    public RepositoryApi? Api { get; set; }
    public CancellationTokenSource? ScheduleCancellation { get; set; }
    public Task ScheduleTask { get; set; } = Task.CompletedTask;
}

/// <summary>ある時点の状態の不変コピー（ロック外で安全に読めるようにする）。</summary>
public sealed record RepositoryView(RepositoryEntry Entry, RepositoryState State, string? Error, RepositoryApi? Api);

/// <summary>操作の結果。Message は画面にそのまま表示できる文言。</summary>
public sealed record HubResult(bool Ok, string Message, string? Id = null);

/// <summary>
/// 複数リポジトリの登録・編集・削除と、リポジトリごとのスケジュール実行の開始/停止を担当する。
/// 登録内容は hub.json に保存する。起動した後で追加・削除しても再起動は不要。
/// </summary>
public sealed class HubService : IRepositoryResolver
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly ServeOptions _options;
    private readonly AccessControl _accessControl;
    private readonly string _dataRootPath;
    private readonly string _workspacesPath;
    private readonly HubStore _store;
    private readonly object _syncRoot = new();
    private readonly List<RepositoryHandle> _handles = [];
    private CancellationToken _applicationStopping = CancellationToken.None;

    public HubService(ServeOptions options, AccessControl accessControl)
    {
        _options = options;
        _accessControl = accessControl;
        _dataRootPath = options.ResolveDataRootPath();
        _workspacesPath = Path.Combine(_dataRootPath, "workspaces");
        _store = new HubStore(_dataRootPath);
    }

    public string DataRootPath => _dataRootPath;

    /// <summary>保存済みの登録を読み込み、スケジュール実行を開始する。停止要求まで待つ。</summary>
    public async Task RunAsync(CancellationToken applicationStopping)
    {
        _applicationStopping = applicationStopping;
        foreach (RepositoryEntry entry in _store.Load().Repositories) Attach(entry);
        ConsoleLogger.WriteInfo($"📚 {_handles.Count} 件のリポジトリを読み込みました（{HubStore.FileName}）。");

        try { await Task.Delay(Timeout.Infinite, applicationStopping); }
        catch (OperationCanceledException) { }

        Task[] tasks;
        lock (_syncRoot) { tasks = _handles.Select(handle => handle.ScheduleTask).ToArray(); }
        await Task.WhenAll(tasks).WaitAsync(StopWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public RepositoryApi? Resolve(string? id)
    {
        lock (_syncRoot)
        {
            return _handles.FirstOrDefault(handle => handle.Entry.Id == id)?.Api;
        }
    }

    /// <summary>登録されているか（クローン中・失敗中も含む）。</summary>
    public RepositoryView? Find(string? id)
    {
        lock (_syncRoot)
        {
            RepositoryHandle? handle = _handles.FirstOrDefault(existing => existing.Entry.Id == id);
            return handle is null ? null : ToView(handle);
        }
    }

    public IReadOnlyList<RepositoryView> Snapshot()
    {
        lock (_syncRoot) { return _handles.Select(ToView).ToList(); }
    }

    private static RepositoryView ToView(RepositoryHandle handle)
        => new(handle.Entry, handle.State, handle.Error, handle.Api);

    /// <summary>既存のローカルリポジトリを登録する。</summary>
    public HubResult AddLocal(string? name, string? path, string? configFileName)
    {
        string? error = HubStore.ValidateName(name) ?? HubStore.ValidateRepositoryPath(path)
            ?? HubStore.ValidateConfigFileName(configFileName ?? _options.ConfigFileName);
        if (error is not null) return new HubResult(false, error);

        string full = Path.GetFullPath(path!.Trim());
        lock (_syncRoot)
        {
            string? limit = CheckAddable(full);
            if (limit is not null) return new HubResult(false, limit);

            RepositoryEntry entry = new(
                HubStore.CreateId(name!, _handles.Select(handle => handle.Entry.Id)),
                name!.Trim(), full, configFileName ?? _options.ConfigFileName);
            Attach(entry);
            Persist();
            return new HubResult(true, $"「{entry.Name}」を登録しました。", entry.Id);
        }
    }

    /// <summary>Git の URL から作業コピーを作って登録する。クローンはバックグラウンドで進み、一覧に「クローン中」と出る。</summary>
    public HubResult AddByClone(string? name, string? gitUrl, string? configFileName)
    {
        string? error = HubStore.ValidateName(name) ?? GitCloner.ValidateUrl(gitUrl)
            ?? HubStore.ValidateConfigFileName(configFileName ?? _options.ConfigFileName);
        if (error is not null) return new HubResult(false, error);

        RepositoryHandle handle;
        lock (_syncRoot)
        {
            string? limit = CheckAddable(null);
            if (limit is not null) return new HubResult(false, limit);
            if (_handles.Any(existing => string.Equals(existing.Entry.CloneUrl, gitUrl!.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return new HubResult(false, "同じ URL のリポジトリが既に登録されています。");
            }

            string id = HubStore.CreateId(name!, _handles.Select(existing => existing.Entry.Id));
            handle = new RepositoryHandle
            {
                Entry = new RepositoryEntry(
                    id, name!.Trim(), Path.Combine(_workspacesPath, id), configFileName ?? _options.ConfigFileName, gitUrl!.Trim()),
                State = RepositoryState.Cloning
            };
            _handles.Add(handle);
            Persist();
        }

        _ = Task.Run(() => CloneAsync(handle));
        return new HubResult(true, $"「{handle.Entry.Name}」のクローンを開始しました。", handle.Entry.Id);
    }

    /// <summary>名前・パス・設定ファイル名を変更する。パスと設定ファイル名は実行中は変えられない。</summary>
    public HubResult Update(string? id, string? name, string? path, string? configFileName)
    {
        lock (_syncRoot)
        {
            RepositoryHandle? handle = _handles.FirstOrDefault(existing => existing.Entry.Id == id);
            if (handle is null) return new HubResult(false, "リポジトリが見つかりません。");
            if (handle.State == RepositoryState.Cloning) return new HubResult(false, "クローン中は変更できません。");

            RepositoryEntry current = handle.Entry;
            string newName = name is null ? current.Name : name.Trim();
            string newConfig = configFileName ?? current.ConfigFileName;
            string newPath = path is null ? current.Path : Path.GetFullPath(path.Trim());

            string? error = HubStore.ValidateName(newName) ?? HubStore.ValidateConfigFileName(newConfig);
            bool pathChanged = !HubStore.IsSamePath(newPath, current.Path);
            if (error is null && pathChanged)
            {
                error = current.CloneUrl is not null
                    ? "ハブがクローンしたリポジトリのパスは変更できません。"
                    : HubStore.ValidateRepositoryPath(newPath)
                        ?? (_handles.Any(other => other != handle && HubStore.IsSamePath(other.Entry.Path, newPath))
                            ? "そのパスは既に別のリポジトリとして登録されています。" : null);
            }

            if (error is not null) return new HubResult(false, error);

            bool needsRestart = pathChanged || newConfig != current.ConfigFileName;
            if (needsRestart && handle.Api?.IsRunning == true)
            {
                return new HubResult(false, "実行中はパスと設定ファイル名を変更できません。完了してからやり直してください。");
            }

            handle.Entry = current with { Name = newName, Path = newPath, ConfigFileName = newConfig };
            if (needsRestart || handle.State == RepositoryState.Error)
            {
                StopSchedules(handle);
                Reattach(handle);
            }
            else if (handle.Api is not null)
            {
                handle.Api.Name = newName; // 名前だけの変更は実行中の状態を保ったまま反映する
            }

            Persist();
            return new HubResult(true, "変更を保存しました。", handle.Entry.Id);
        }
    }

    /// <summary>登録を外す。ディスク上のファイル（リポジトリ本体・履歴）は削除しない。実行中は外せない。</summary>
    public HubResult Remove(string? id)
    {
        RepositoryHandle handle;
        lock (_syncRoot)
        {
            RepositoryHandle? found = _handles.FirstOrDefault(existing => existing.Entry.Id == id);
            if (found is null) return new HubResult(false, "リポジトリが見つかりません。");
            if (found.State == RepositoryState.Cloning) return new HubResult(false, "クローン中は削除できません。");
            if (found.Api?.IsRunning == true) return new HubResult(false, "実行中は削除できません。完了してからやり直してください。");

            handle = found;
            _handles.Remove(handle);
            Persist();
        }

        StopSchedules(handle);
        return new HubResult(true, $"「{handle.Entry.Name}」を登録から外しました（ファイルは削除していません）。");
    }

    private string? CheckAddable(string? fullPath)
    {
        if (_handles.Count >= HubStore.MaxRepositories) return $"登録できるのは {HubStore.MaxRepositories} 件までです。";
        return fullPath is not null && _handles.Any(handle => HubStore.IsSamePath(handle.Entry.Path, fullPath))
            ? "そのパスは既に登録されています。"
            : null;
    }

    /// <summary>登録情報から実行時の状態を作り、問題なければスケジュール実行を開始する。呼び出し側で排他すること。</summary>
    private void Attach(RepositoryEntry entry)
    {
        RepositoryHandle handle = new() { Entry = entry };
        _handles.Add(handle);
        Reattach(handle);
    }

    private void Reattach(RepositoryHandle handle)
    {
        RepositoryEntry entry = handle.Entry;
        string? error = HubStore.ValidateRepositoryPath(entry.Path);
        if (error is not null)
        {
            handle.State = RepositoryState.Error;
            handle.Error = entry.CloneUrl is not null ? "クローンが完了していません。削除して登録し直してください。" : error;
            handle.Api = null;
            return;
        }

        handle.State = RepositoryState.Ready;
        handle.Error = null;
        handle.Api = new RepositoryApi(
            entry.Name, entry.Path, Path.Combine(entry.Path, HistoryManager.DefaultDataDirectoryName),
            entry.ConfigFileName, _accessControl);
        handle.ScheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        handle.ScheduleTask = Task.Run(() => handle.Api.RunSchedulesAsync(handle.ScheduleCancellation.Token));
    }

    private static void StopSchedules(RepositoryHandle handle)
    {
        handle.ScheduleCancellation?.Cancel();
        try { handle.ScheduleTask.Wait(StopWait); }
        catch (AggregateException) { }
        handle.ScheduleCancellation?.Dispose();
        handle.ScheduleCancellation = null;
    }

    private async Task CloneAsync(RepositoryHandle handle)
    {
        RepositoryEntry entry = handle.Entry;
        (bool success, string output) = await RunCloneAsync(entry);
        lock (_syncRoot)
        {
            if (!_handles.Contains(handle)) return;

            if (success)
            {
                Reattach(handle);
                ConsoleLogger.WriteSuccess($"✅ 「{entry.Name}」のクローンが完了しました。");
            }
            else
            {
                handle.State = RepositoryState.Error;
                handle.Error = $"クローンに失敗しました: {output}";
                ConsoleLogger.WriteWarning($"⚠ 「{entry.Name}」のクローンに失敗しました。");
            }
        }
    }

    private async Task<(bool, string)> RunCloneAsync(RepositoryEntry entry)
    {
        try
        {
            if (Directory.Exists(entry.Path) && Directory.EnumerateFileSystemEntries(entry.Path).Any())
            {
                return (false, $"作業ディレクトリが既に存在し空ではありません: {entry.Path}");
            }

            Directory.CreateDirectory(_workspacesPath);
            return await GitCloner.CloneAsync(entry.CloneUrl!, entry.Path, _applicationStopping);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, exception.Message);
        }
    }

    /// <summary>登録内容を hub.json に保存する。呼び出し側で排他すること。</summary>
    private void Persist()
        => _store.Save(new HubData { Repositories = _handles.Select(handle => handle.Entry).ToList() });
}
