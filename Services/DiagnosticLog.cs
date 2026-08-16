namespace SoulBuddy.Services;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = BuildLogDirectory();

    public static string FilePath { get; } = Path.Combine(LogDirectory, "soulbuddy-debug.log");

    public static void StartSession(string context)
    {
        Write("INFO", "Startup", new string('-', 72));
        Write("INFO", "Startup", $"Diagnostic session started: {context}");
        Write("INFO", "Startup", $"PID={Environment.ProcessId}; OS={Environment.OSVersion}; .NET={Environment.Version}");
        Write("INFO", "Startup", $"Log file: {FilePath}");
    }

    public static void Info(string category, string message) =>
        Write("INFO", category, message);

    public static void Warning(string category, string message) =>
        Write("WARN", category, message);

    public static void Error(string category, string message) =>
        Write("ERROR", category, message);

    public static void Exception(string category, string message, Exception exception) =>
        Write(
            "ERROR",
            category,
            $"{message} | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    private static void Write(string level, string category, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                TrimOversizedLog();

                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                File.AppendAllText(
                    FilePath,
                    $"[{timestamp}] [{level}] [{category}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never be able to break SoulBuddy.
        }
    }

    private static void TrimOversizedLog()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > 5 * 1024 * 1024)
                File.Delete(FilePath);
        }
        catch
        {
        }
    }

    private static string BuildLogDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Path.GetTempPath();

        return Path.Combine(localAppData, "SoulBuddy", "logs");
    }
}
