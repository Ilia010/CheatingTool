using System;
using System.IO;

namespace CheatingTool;

/// <summary>Пути приложения. Все данные лежат в %LOCALAPPDATA%\CheatingTool.</summary>
internal static class Paths
{
    public const string ProductName = "CheatingTool";

    private const string OldFolderName = "appka";

    private static readonly object Sync = new();
    private static string? _dataDir;

    /// <summary>Папка с настройками, логом и профилем Chrome.</summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;

            lock (Sync)
            {
                _dataDir ??= PrepareDataDir();
                return _dataDir;
            }
        }
    }

    private static string PrepareDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, ProductName);
        var legacy = Path.Combine(local, OldFolderName);

        if (Directory.Exists(current) || !Directory.Exists(legacy))
            return current;

        // Один раз переносим данные из старой версии (appka), чтобы не потерять вход в Chrome.
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch
        {
            try
            {
                CopyDirectory(legacy, current);
                return current;
            }
            catch
            {
                return current;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
