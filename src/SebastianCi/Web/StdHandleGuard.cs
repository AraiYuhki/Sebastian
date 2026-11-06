using System.Runtime.InteropServices;

namespace SebastianCi.Web;

/// <summary>
/// Windows で、呼び出し元の標準入出力ハンドルを一時的に「子プロセスへ継承されない」状態にする。
/// 常駐プロセスを起動するとき、これをしないと呼び出し側のパイプ（`daemon start | tee` や `$(…)` の出力先）を
/// 常駐プロセスが握ったままになり、常駐が終わるまでパイプが閉じず呼び出し側が固まる。
/// Windows 以外では何もしない（fork/exec では継承されない）。using の終わりで元に戻す。
/// </summary>
public sealed class StdHandleGuard : IDisposable
{
    private const uint HandleFlagInherit = 1;
    private static readonly int[] StdHandleIds = [-10, -11, -12]; // STD_INPUT / OUTPUT / ERROR

    private readonly List<IntPtr> _changed = [];

    private StdHandleGuard() { }

    /// <summary>標準ハンドルの継承を止める。戻り値を Dispose すると元の状態に戻る。</summary>
    public static StdHandleGuard SuppressInheritance()
    {
        StdHandleGuard guard = new();
        if (!OperatingSystem.IsWindows()) return guard;

        foreach (int id in StdHandleIds)
        {
            IntPtr handle = GetStdHandle(id);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
            if (!GetHandleInformation(handle, out uint flags) || (flags & HandleFlagInherit) == 0) continue;
            if (SetHandleInformation(handle, HandleFlagInherit, 0)) guard._changed.Add(handle);
        }

        return guard;
    }

    public void Dispose()
    {
        foreach (IntPtr handle in _changed) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
        _changed.Clear();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
