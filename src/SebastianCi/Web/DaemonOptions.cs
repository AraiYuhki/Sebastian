namespace SebastianCi.Web;

public enum DaemonAction { Start, Stop, Restart, Status, Run }

/// <summary>
/// `daemon` サブコマンドの引数。`daemon &lt;start|stop|restart|status|run&gt; [serve と同じ引数]`。
/// run はスーパーバイザー本体（systemd 等から前面で動かす用途にも使える）。
/// </summary>
public sealed record DaemonOptions(DaemonAction Action, ServeOptions Serve)
{
    public const string Usage =
        "使い方: sebastian-ci daemon <start|stop|restart|status|run> [リポジトリパス] [--port <番号>]"
        + " [--config <ファイル名>] [--data-dir <パス>] [--token <トークン>]";

    public static DaemonOptions? Parse(string[] args)
    {
        if (args.Length == 0) return null;

        DaemonAction? action = args[0] switch
        {
            "start" => DaemonAction.Start,
            "stop" => DaemonAction.Stop,
            "restart" => DaemonAction.Restart,
            "status" => DaemonAction.Status,
            "run" => DaemonAction.Run,
            _ => null
        };
        if (action is null) return null;

        ServeOptions? serve = ServeOptions.Parse(args[1..]);
        return serve is null ? null : new DaemonOptions(action.Value, serve);
    }
}
