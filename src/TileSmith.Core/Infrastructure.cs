using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TileSmith.Core;

/// <summary>Well-known folders. Data lives in %AppData%\TileSmith so it survives updates and reinstalls.</summary>
public static class AppPaths
{
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("TILESMITH_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TileSmith");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string WidgetsFile => Path.Combine(DataDir, "widgets.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string KeysDir => Path.Combine(DataDir, "keys");
}

public static class JsonDefaults
{
    /// <summary>Lenient options: AI output often has trailing commas, comments or numbers-as-strings.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>Tiny file logger. One file per day in %AppData%\TileSmith\logs, kept for a week.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string? CurrentFile => _file;

    public static void Init(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _file = Path.Combine(directory, $"tilesmith-{DateTime.Now:yyyyMMdd}.log");
            foreach (var old in Directory.EnumerateFiles(directory, "tilesmith-*.log"))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old);
            }
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        if (_file is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never crash the app */ }
        }
    }
}

/// <summary>Writes files atomically so a crash mid-save never corrupts settings or widgets.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
