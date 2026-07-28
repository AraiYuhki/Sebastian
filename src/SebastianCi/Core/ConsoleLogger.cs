namespace SebastianCi.Core;

/// <summary>
/// 並列実行中でも行単位で崩れない、色付きコンソール出力を提供する。
/// </summary>
public static class ConsoleLogger
{
    private static readonly object SyncRoot = new();

    public static void WriteInfo(string message) => WriteWithColor(message, ConsoleColor.Cyan);

    public static void WriteSuccess(string message) => WriteWithColor(message, ConsoleColor.Green);

    public static void WriteWarning(string message) => WriteWithColor(message, ConsoleColor.Yellow);

    public static void WriteError(string message) => WriteWithColor(message, ConsoleColor.Red);

    /// <summary>
    /// コンテナからの出力1行を、ジョブIDのプレフィックス付きで表示する。
    /// ホスト環境変数から展開された秘密情報が含まれていれば伏せ字にする。
    /// </summary>
    public static void WriteJobOutput(string jobId, string line, bool isError)
    {
        line = SecretMasker.Mask(line);
        lock (SyncRoot)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{jobId}] ");
            Console.ForegroundColor = isError ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.WriteLine(line);
            Console.ResetColor();
        }
    }

    private static void WriteWithColor(string message, ConsoleColor color)
    {
        lock (SyncRoot)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}
