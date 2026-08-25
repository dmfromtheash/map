using System;
using System.IO;
using System.Text;


namespace Map.Spike.App.Diagnostics;

/// <summary>
/// Writes unhandled exceptions to a file next to the spike's other output.
///
/// A GUI process has no console, so without this an unhandled exception is only visible as
/// "exception code 0xE0434352" in the Windows event log, which says nothing about what failed.
/// The log holds a stack trace and no map content (docs/04 diagnostics rule).
/// </summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Map",
        "Spike",
        "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);

            var text = new StringBuilder()
                .AppendLine($"--- {DateTimeOffset.UtcNow:O} {source} ---")
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();

            File.AppendAllText(Path, text);
        }
        catch (IOException)
        {
            // Losing the crash log must not itself crash the crash handler.
        }
    }
}
