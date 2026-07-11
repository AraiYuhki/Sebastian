using System.Text.Json;
using System.Text.Json.Serialization;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 実行履歴の永続化だけを担当する。
/// コミットハッシュをキーとしたメタデータを .sebastian-ci/history.json に保存・更新し、
/// ジョブログの置き場（.sebastian-ci/logs/[commit_hash]/）を用意する。
/// </summary>
public sealed class HistoryManager
{
    public const string DefaultDataDirectoryName = ".sebastian-ci";

    private const string HistoryFileName = "history.json";
    private const string LogsDirectoryName = "logs";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataRootPath;
    private readonly string _historyFilePath;
    private Dictionary<string, BuildRecord>? _cachedHistory;

    public HistoryManager(string dataRootPath)
    {
        _dataRootPath = dataRootPath;
        _historyFilePath = Path.Combine(dataRootPath, HistoryFileName);
    }

    /// <summary>指定コミットが過去に成功しているか（スキップ判定に使う）を返す。</summary>
    public async Task<bool> HasSuccessRecordAsync(string commitHash, CancellationToken cancellationToken = default)
    {
        Dictionary<string, BuildRecord> history = await LoadHistoryAsync(cancellationToken);
        return history.TryGetValue(commitHash, out BuildRecord? record) && record.IsSuccess;
    }

    /// <summary>指定コミット用のログディレクトリを作成し、そのパスを返す。</summary>
    public string PrepareLogDirectory(string commitHash)
    {
        string logDirectoryPath = Path.Combine(_dataRootPath, LogsDirectoryName, commitHash);
        Directory.CreateDirectory(logDirectoryPath);
        return logDirectoryPath;
    }

    /// <summary>実行結果（成否を問わず）をコミットハッシュをキーに保存・更新する。</summary>
    public async Task SaveRecordAsync(string commitHash, BuildRecord record, CancellationToken cancellationToken = default)
    {
        Dictionary<string, BuildRecord> history = await LoadHistoryAsync(cancellationToken);
        history[commitHash] = record;

        Directory.CreateDirectory(_dataRootPath);
        string json = JsonSerializer.Serialize(history, SerializerOptions);
        await File.WriteAllTextAsync(_historyFilePath, json, cancellationToken);
    }

    private async Task<Dictionary<string, BuildRecord>> LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (_cachedHistory is not null) return _cachedHistory;
        if (!File.Exists(_historyFilePath)) return _cachedHistory = new Dictionary<string, BuildRecord>();

        string json = await File.ReadAllTextAsync(_historyFilePath, cancellationToken);
        return _cachedHistory = DeserializeHistory(json);
    }

    private static Dictionary<string, BuildRecord> DeserializeHistory(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, BuildRecord>>(json, SerializerOptions)
                ?? new Dictionary<string, BuildRecord>();
        }
        catch (JsonException exception)
        {
            ConsoleLogger.WriteWarning($"⚠ history.json が破損しているため履歴を初期化します: {exception.Message}");
            return new Dictionary<string, BuildRecord>();
        }
    }
}
