namespace UpgradePrAgent;

/// <summary>Simple console logger.</summary>
public static class Log
{
    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Write("DBG", message, ConsoleColor.DarkGray);
    }

    public static void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public static void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        var ts = DateTime.UtcNow.ToString("HH:mm:ss");
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Error.WriteLine($"[{ts} {level}] {message}");
        Console.ForegroundColor = prev;
    }
}
