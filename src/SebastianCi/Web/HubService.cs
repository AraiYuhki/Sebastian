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

/// <summary>連鎖の直近の動き（メモリ上のみ。再起動で消える）。</summary>
public sealed record ChainStatus(DateTimeOffset? LastTriggeredAt, string? LastOutcome, int Pending);

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
    private readonly string _jobsPath;
    private readonly HubStore _store;
    private readonly object _syncRoot = new();
    private readonly List<RepositoryHandle> _handles = [];
    private readonly List<ChainEntry> _chains = [];
    private readonly Dictionary<string, (DateTimeOffset At, string Outcome)> _chainOutcomes = [];
    private readonly Dictionary<string, List<PendingTrigger>> _pendingTriggers = [];
    private CancellationToken _applicationStopping = CancellationToken.None;

    public HubService(ServeOptions options, AccessControl accessControl)
    {
        _options = options;
        _accessControl = accessControl;
        _dataRootPath = options.ResolveDataRootPath();
        _workspacesPath = Path.Combine(_dataRootPath, "workspaces");
        _jobsPath = Path.Combine(_dataRootPath, "jobs");
        _store = new HubStore(_dataRootPath);
    }

    /// <summary>連鎖が辿れる段数の上限。循環は登録時に拒否するが、設定ファイルを手で編集された場合の最後の歯止め。</summary>
    private const int MaxChainDepth = 10;
    private const int MaxPendingPerTarget = 50;

    /// <summary>起動先が実行中で待たされている連鎖の起動要求。</summary>
    private sealed record PendingTrigger(ChainEntry Chain, string FromName, bool UpstreamSucceeded, List<string> Path);

    public string DataRootPath => _dataRootPath;

    /// <summary>保存済みの登録を読み込み、スケジュール実行を開始する。停止要求まで待つ。</summary>
    public async Task RunAsync(CancellationToken applicationStopping)
    {
        _applicationStopping = applicationStopping;
        HubData saved = _store.Load();
        lock (_syncRoot)
        {
            _chains.AddRange(saved.Chains ?? []);
            foreach (RepositoryEntry entry in saved.Repositories) Attach(entry);
        }

        ConsoleLogger.WriteInfo($"📚 {_handles.Count} 件のリポジトリ・ジョブと {_chains.Count} 件の連鎖を読み込みました（{HubStore.FileName}）。");

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

    private const string WorkspaceDefinitionFileName = "job.yaml";

    /// <summary>
    /// ワークスペース型ジョブを作る。jobs/&lt;id&gt;/ に定義ファイル（job.yaml）・作業場（workspace）・履歴（data）を用意し、
    /// 実行のたびに sources を workspace へ取り込む。リポジトリには何も書き込まない。
    /// </summary>
    public HubResult AddWorkspaceJob(string? name, IReadOnlyList<SourcePayload>? sources)
    {
        (List<SourceEntry>? normalized, string? sourceError) = HubStore.NormalizeSources(sources);
        string? error = HubStore.ValidateName(name) ?? sourceError;
        if (error is not null) return new HubResult(false, error);

        lock (_syncRoot)
        {
            string? limit = CheckAddable(null);
            if (limit is not null) return new HubResult(false, limit);

            // 登録から外した過去のジョブのディレクトリ（定義・履歴）を、新しいジョブが引き継がないよう ID を避ける。
            IEnumerable<string> usedIds = _handles.Select(handle => handle.Entry.Id).Concat(ExistingJobDirectoryNames());
            string id = HubStore.CreateId(name!, usedIds, "job");
            string jobDirectory = Path.Combine(_jobsPath, id);
            try
            {
                Directory.CreateDirectory(Path.Combine(jobDirectory, "workspace"));
                Directory.CreateDirectory(Path.Combine(jobDirectory, "data"));
                File.WriteAllText(Path.Combine(jobDirectory, WorkspaceDefinitionFileName), ConfigEditor.StarterTemplate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new HubResult(false, $"ジョブのディレクトリを作成できません: {exception.Message}");
            }

            RepositoryEntry entry = new(
                id, name!.Trim(), Path.Combine(jobDirectory, "workspace"),
                Path.Combine(jobDirectory, WorkspaceDefinitionFileName), null, normalized);
            Attach(entry);
            Persist();
            return new HubResult(true, $"ジョブ「{entry.Name}」を作成しました。", entry.Id);
        }
    }

    private IEnumerable<string> ExistingJobDirectoryNames()
        => Directory.Exists(_jobsPath)
            ? Directory.EnumerateDirectories(_jobsPath).Select(path => Path.GetFileName(path))
            : [];

    private string DataDirectoryOf(RepositoryEntry entry)
        => entry.IsWorkspaceJob
            ? Path.Combine(_jobsPath, entry.Id, "data")
            : Path.Combine(entry.Path, HistoryManager.DefaultDataDirectoryName);

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
    public HubResult Update(
        string? id, string? name, string? path, string? configFileName, IReadOnlyList<SourcePayload>? sources = null)
    {
        lock (_syncRoot)
        {
            RepositoryHandle? handle = _handles.FirstOrDefault(existing => existing.Entry.Id == id);
            if (handle is null) return new HubResult(false, "リポジトリが見つかりません。");
            if (handle.State == RepositoryState.Cloning) return new HubResult(false, "クローン中は変更できません。");

            RepositoryEntry current = handle.Entry;
            if (current.IsWorkspaceJob) return UpdateWorkspaceJob(handle, name, sources);

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

    /// <summary>ワークスペース型ジョブの名前とソースを変更する。ソースの変更は実行中はできない。</summary>
    private HubResult UpdateWorkspaceJob(RepositoryHandle handle, string? name, IReadOnlyList<SourcePayload>? sources)
    {
        RepositoryEntry current = handle.Entry;
        string newName = name is null ? current.Name : name.Trim();
        string? error = HubStore.ValidateName(newName);
        List<SourceEntry>? newSources = null;
        if (error is null && sources is not null)
        {
            (newSources, error) = HubStore.NormalizeSources(sources);
        }

        if (error is not null) return new HubResult(false, error);

        bool sourcesChanged = newSources is not null && !SourcesEqual(current.Sources!, newSources);
        if (sourcesChanged && handle.Api?.IsRunning == true)
        {
            return new HubResult(false, "実行中はソースを変更できません。完了してからやり直してください。");
        }

        handle.Entry = current with { Name = newName, Sources = sourcesChanged ? newSources : current.Sources };
        if (sourcesChanged)
        {
            StopSchedules(handle);
            Reattach(handle);
        }
        else if (handle.Api is not null)
        {
            handle.Api.Name = newName;
        }

        Persist();
        return new HubResult(true, "変更を保存しました。", handle.Entry.Id);
    }

    private static bool SourcesEqual(IReadOnlyList<SourceEntry> a, IReadOnlyList<SourceEntry> b)
        => a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);

    /// <summary>登録を外す。ディスク上のファイル（リポジトリ本体・履歴）は削除しない。実行中は外せない。</summary>
    public HubResult Remove(string? id)
    {
        RepositoryHandle handle;
        int removedChains;
        lock (_syncRoot)
        {
            RepositoryHandle? found = _handles.FirstOrDefault(existing => existing.Entry.Id == id);
            if (found is null) return new HubResult(false, "リポジトリが見つかりません。");
            if (found.State == RepositoryState.Cloning) return new HubResult(false, "クローン中は削除できません。");
            if (found.Api?.IsRunning == true) return new HubResult(false, "実行中は削除できません。完了してからやり直してください。");

            handle = found;
            _handles.Remove(handle);
            removedChains = _chains.RemoveAll(chain => chain.From == handle.Entry.Id || chain.To == handle.Entry.Id);
            _pendingTriggers.Remove(handle.Entry.Id);
            foreach (List<PendingTrigger> queue in _pendingTriggers.Values)
            {
                queue.RemoveAll(trigger => trigger.Chain.From == handle.Entry.Id);
            }

            Persist();
        }

        StopSchedules(handle);
        return new HubResult(true, $"「{handle.Entry.Name}」を登録から外しました（ファイルは削除していません）。"
            + (removedChains > 0 ? $" 関連する連鎖 {removedChains} 件も削除しました。" : ""));
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
        if (entry.IsWorkspaceJob)
        {
            AttachWorkspaceJob(handle);
            return;
        }

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
            entry.Name, entry.Path, DataDirectoryOf(entry), entry.ConfigFileName, _accessControl);
        SubscribeCompletion(handle);
        StartSchedules(handle);
    }

    private void AttachWorkspaceJob(RepositoryHandle handle)
    {
        RepositoryEntry entry = handle.Entry;
        try
        {
            Directory.CreateDirectory(entry.Path); // 作業場を手で消されていても、次の実行で取り直せるようにする
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            handle.State = RepositoryState.Error;
            handle.Error = $"作業場を用意できません: {exception.Message}";
            handle.Api = null;
            return;
        }

        List<SourceEntry> sources = entry.Sources!;
        handle.State = RepositoryState.Ready;
        handle.Error = null;
        handle.Api = new RepositoryApi(
            entry.Name, entry.Path, DataDirectoryOf(entry), entry.ConfigFileName, _accessControl,
            (log, cancellationToken) => WorkspaceSync.PrepareAsync(entry.Path, sources, log, cancellationToken),
            _applicationStopping);
        SubscribeCompletion(handle);
        StartSchedules(handle);
    }

    private void StartSchedules(RepositoryHandle handle)
    {
        RepositoryApi api = handle.Api!;
        handle.ScheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        CancellationToken token = handle.ScheduleCancellation.Token;
        handle.ScheduleTask = Task.Run(() => api.RunSchedulesAsync(token));
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


    // ================= 連鎖（ジョブ間のトリガー） =================

    /// <summary>連鎖の一覧と、それぞれの直近の動き（管理画面用）。</summary>
    public IReadOnlyList<(ChainEntry Chain, ChainStatus Status)> ChainSnapshot()
    {
        lock (_syncRoot)
        {
            return _chains.Select(chain => (chain, StatusOf(chain))).ToList();
        }
    }

    private ChainStatus StatusOf(ChainEntry chain)
    {
        int pending = _pendingTriggers.Values.Sum(queue => queue.Count(trigger => trigger.Chain.Id == chain.Id));
        return _chainOutcomes.TryGetValue(chain.Id, out (DateTimeOffset At, string Outcome) last)
            ? new ChainStatus(last.At, last.Outcome, pending)
            : new ChainStatus(null, null, pending);
    }

    public HubResult AddChain(ChainPayload payload)
    {
        lock (_syncRoot)
        {
            (ChainEntry? chain, string? error) = BuildChain(null, payload);
            if (chain is null) return new HubResult(false, error!);

            _chains.Add(chain);
            Persist();
            return new HubResult(true, "連鎖を追加しました。", chain.Id);
        }
    }

    public HubResult UpdateChain(string? id, ChainPayload payload)
    {
        lock (_syncRoot)
        {
            ChainEntry? existing = _chains.FirstOrDefault(chain => chain.Id == id);
            if (existing is null) return new HubResult(false, "連鎖が見つかりません。");

            (ChainEntry? chain, string? error) = BuildChain(existing, payload);
            if (chain is null) return new HubResult(false, error!);

            _chains[_chains.IndexOf(existing)] = chain;
            if (!chain.Enabled || chain.To != existing.To)
            {
                foreach (List<PendingTrigger> queue in _pendingTriggers.Values) queue.RemoveAll(t => t.Chain.Id == chain.Id);
            }

            Persist();
            return new HubResult(true, "連鎖を保存しました。", chain.Id);
        }
    }

    public HubResult RemoveChain(string? id)
    {
        lock (_syncRoot)
        {
            if (_chains.RemoveAll(chain => chain.Id == id) == 0) return new HubResult(false, "連鎖が見つかりません。");

            foreach (List<PendingTrigger> queue in _pendingTriggers.Values) queue.RemoveAll(trigger => trigger.Chain.Id == id);
            _chainOutcomes.Remove(id!);
            Persist();
            return new HubResult(true, "連鎖を削除しました。");
        }
    }

    /// <summary>入力を検証して連鎖を作る。existing があれば、null の項目はその値を引き継ぐ。呼び出し側で排他すること。</summary>
    private (ChainEntry? Chain, string? Error) BuildChain(ChainEntry? existing, ChainPayload payload)
    {
        string from = payload.From ?? existing?.From ?? "";
        string to = payload.To ?? existing?.To ?? "";
        string on = (payload.On ?? existing?.On ?? "success").Trim().ToLowerInvariant();
        string? job = payload.Job is null ? existing?.Job : (string.IsNullOrWhiteSpace(payload.Job) ? null : payload.Job.Trim());
        Dictionary<string, string>? parameters = payload.Params is null ? existing?.Params : payload.Params;
        if (parameters is { Count: 0 }) parameters = null;

        RepositoryHandle? upstream = _handles.FirstOrDefault(handle => handle.Entry.Id == from);
        RepositoryHandle? downstream = _handles.FirstOrDefault(handle => handle.Entry.Id == to);
        string? error = upstream is null ? "起動元（このジョブが終わったら）が見つかりません。"
            : downstream is null ? "起動先（次に実行するもの）が見つかりません。"
            : from == to ? "起動元と起動先に同じものは指定できません。"
            : HubStore.ValidateChainFields(on, job, parameters);
        if (error is null && existing is null && _chains.Count >= HubStore.MaxChains) error = $"登録できる連鎖は {HubStore.MaxChains} 件までです。";
        if (error is null && job is not null && downstream!.Api is { } api && !api.HasJob(job))
        {
            error = $"起動先に「{job}」というジョブがありません。";
        }

        if (error is null && HubStore.WouldCreateCycle(_chains, from, to, existing?.Id))
        {
            error = $"この連鎖を足すと、「{upstream!.Entry.Name}」から「{downstream!.Entry.Name}」へ辿った先が元に戻り、ぐるぐる回ってしまいます。";
        }

        if (error is not null) return (null, error);

        ChainEntry chain = new(
            existing?.Id ?? NextChainId(), from, to, on, job, parameters,
            payload.Rebuild ?? existing?.Rebuild ?? true, payload.Yes ?? existing?.Yes ?? false,
            payload.Enabled ?? existing?.Enabled ?? true);
        return (chain, null);
    }

    private string NextChainId()
    {
        int max = _chains
            .Select(chain => chain.Id.StartsWith('c') && int.TryParse(chain.Id[1..], out int n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"c{max + 1}";
    }

    private void SubscribeCompletion(RepositoryHandle handle)
    {
        string id = handle.Entry.Id;
        handle.Api!.RunCompleted += completion => OnRunCompleted(id, completion);
    }

    /// <summary>
    /// 実行が終わったとき、条件に合う連鎖の起動先を実行する。起動先が実行中なら待機列に入れ、その実行が終わった時点で始める。
    /// 「実行済みでスキップ」した実行（ジョブを1つも動かしていない）は、成功とも失敗とも見なさず連鎖を起動しない。
    /// </summary>
    private void OnRunCompleted(string entryId, RunCompletion completion)
    {
        lock (_syncRoot)
        {
            List<string> targets = [entryId]; // 終わった本人の待機分も、ここで改めて確認する
            string fromName = _handles.FirstOrDefault(handle => handle.Entry.Id == entryId)?.Entry.Name ?? entryId;
            if (!completion.Skipped)
            {
                foreach (ChainEntry chain in _chains.Where(chain => chain.Enabled && chain.From == entryId && Matches(chain.On, completion)).ToList())
                {
                    if (Enqueue(chain, fromName, completion, entryId)) targets.Add(chain.To);
                }
            }

            foreach (string target in targets.Distinct()) Drain(target);
        }
    }

    private static bool Matches(string on, RunCompletion completion)
        => on == "always" || (on == "success" && completion.IsSuccess) || (on == "failure" && !completion.IsSuccess);

    private bool Enqueue(ChainEntry chain, string fromName, RunCompletion completion, string entryId)
    {
        List<string> path = [.. completion.ChainPath, entryId];
        if (path.Contains(chain.To) || path.Count >= MaxChainDepth)
        {
            Record(chain, "連鎖が循環している（または深すぎる）ため起動しませんでした。");
            ConsoleLogger.WriteWarning($"⚠ 連鎖 {chain.Id}: 循環または段数の上限のため起動を見送りました。");
            return false;
        }

        if (_handles.FirstOrDefault(handle => handle.Entry.Id == chain.To)?.Api is null)
        {
            Record(chain, "起動先が利用できないため起動できませんでした。");
            return false;
        }

        if (!_pendingTriggers.TryGetValue(chain.To, out List<PendingTrigger>? queue))
        {
            queue = [];
            _pendingTriggers[chain.To] = queue;
        }

        if (queue.Any(trigger => trigger.Chain.Id == chain.Id))
        {
            Record(chain, "同じ連鎖が既に待機中です。");
            return false;
        }

        if (queue.Count >= MaxPendingPerTarget)
        {
            Record(chain, "待機が多すぎるため起動しませんでした。");
            return false;
        }

        queue.Add(new PendingTrigger(chain, fromName, completion.IsSuccess, path));
        Record(chain, "起動待ち");
        return true;
    }

    /// <summary>target の待機列の先頭を、target が空いていれば起動する。1回の実行が終わるたびに呼ぶ。呼び出し側で排他すること。</summary>
    private void Drain(string target)
    {
        if (!_pendingTriggers.TryGetValue(target, out List<PendingTrigger>? queue) || queue.Count == 0) return;

        RepositoryApi? api = _handles.FirstOrDefault(handle => handle.Entry.Id == target)?.Api;
        if (api is null)
        {
            queue.Clear();
            return;
        }

        PendingTrigger next = queue[0];
        string result = next.UpstreamSucceeded ? "成功" : "失敗";
        string note = $"🔗 連鎖: 「{next.FromName}」が{result}したため起動されました。";
        if (!api.TryStartChained(next.Chain.Rebuild, next.Chain.Job, next.Chain.Params, next.Chain.Yes, next.Path, note))
        {
            foreach (PendingTrigger waiting in queue) Record(waiting.Chain, "待機中（起動先が実行中）");
            return; // 起動先の今の実行が終わったときに、改めてここへ来る
        }

        queue.RemoveAt(0);
        Record(next.Chain, "起動しました");
        ConsoleLogger.WriteInfo($"🔗 連鎖 {next.Chain.Id}: 「{next.FromName}」の{result}を受けて、「{target}」を起動しました。");
    }

    private void Record(ChainEntry chain, string outcome) => _chainOutcomes[chain.Id] = (DateTimeOffset.Now, outcome);

    /// <summary>登録内容を hub.json に保存する。呼び出し側で排他すること。</summary>
    private void Persist()
        => _store.Save(new HubData { Repositories = _handles.Select(handle => handle.Entry).ToList(), Chains = _chains.ToList() });
}
