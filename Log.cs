using System;
using System.IO;

namespace CheatingTool;

internal static class Log
{
    private static readonly object Sync = new();
    private static string _path = "";

    public static string DirectoryPath => Paths.DataDir;

    public static void Init()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            _path = Path.Combine(DirectoryPath, "CheatingTool.log");
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 1_500_000)
            {
                var old = _path + ".old";
                try { File.Delete(old); } catch { }
                File.Move(_path, old);
            }
        }
        catch
        {
            _path = "";
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(_path)) return;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        Write(ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
    }
}
