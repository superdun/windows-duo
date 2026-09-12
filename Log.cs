using System.IO;
using SharpGen.Runtime;

namespace WindowsDuo;

internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(Path.GetTempPath(), "windows-duo.log");

    public static void StartSession()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
        }

        Info($"---- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ----");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static string Describe(Exception ex)
    {
        if (ex is SharpGenException sg)
        {
            return $"{sg.Message}  hr=0x{sg.HResult:X8}";
        }

        return ex.ToString();
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] T{Environment.CurrentManagedThreadId} {message}";
            if (ex is not null)
            {
                line += Environment.NewLine + Describe(ex);
                if (ex.StackTrace is not null)
                {
                    line += Environment.NewLine + ex.StackTrace;
                }
            }

            lock (Gate)
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
