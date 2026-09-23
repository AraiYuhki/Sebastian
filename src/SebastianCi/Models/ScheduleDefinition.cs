namespace SebastianCi.Models;

/// <summary>
/// ダッシュボード（serve）のスケジュール実行機能で使う1件分の定義。
/// CLI単体（sebastian-ci コマンド）の実行では参照されず、serve プロセス内の ScheduleRunner だけが解釈する。
/// </summary>
public sealed class ScheduleDefinition
{
    /// <summary>標準的な5フィールドのcron式（分 時 日 月 曜日）。</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>実行するジョブ名。省略時はパイプライン全体（--job 無し）を実行する。</summary>
    public string? Job { get; set; }

    /// <summary>実行時パラメーター（--param 相当）。</summary>
    public Dictionary<string, string> Params { get; set; } = new();

    /// <summary>承認ゲートを自動承認するか（--yes 相当）。</summary>
    public bool Yes { get; set; }

    /// <summary>実行済みのコミットでも再実行するか（--rebuild 相当）。</summary>
    public bool Rebuild { get; set; }

    /// <summary>false にすると、定義を残したまま一時的に無効化できる。</summary>
    public bool Enabled { get; set; } = true;
}
