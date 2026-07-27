using System.IO;
using System.Runtime.InteropServices;

namespace AgentRelay.App;

internal static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void Attach()
    {
        if (AttachConsole(AttachParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    public static void Hide()
    {
        var window = GetConsoleWindow();
        if (window != IntPtr.Zero)
        {
            ShowWindow(window, 0);
        }

        // On Windows 11 the default terminal host can keep a blank Windows
        // Terminal window alive even after hiding the legacy console HWND.
        // GUI mode has no console work to perform, so detach from the console
        // entirely. CLI mode never calls Hide() and remains attached.
        FreeConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
