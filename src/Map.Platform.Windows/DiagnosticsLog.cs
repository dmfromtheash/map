using System.Text;
using System.Text.RegularExpressions;

namespace Map.Platform.Windows;

/// <summary>
/// Local, bounded, append-only diagnostics log: startup, open/save/recovery results and unhandled
/// exceptions. It never records document content and never sends anything anywhere (docs/04
/// "diagnostics without telemetry").
/// </summary>
public sealed partial class DiagnosticsLog
{
    private const long MaxBytes = 256 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();

    public DiagnosticsLog(string? directory = null)
    {
        var root = directory ?? AppPaths.DiagnosticsDirectory;
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "diagnostics.log");
    }

    public string Path_ => _path;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"exception={exception.GetType().Name} {message}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.UtcNow:O} [{level}] {Sanitize(message)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        oneLine = WindowsAbsolutePath().Replace(oneLine, "[path-redacted]");
        return oneLine.Length <= 1024 ? oneLine : oneLine[..1024];
    }

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n]*")]
    private static partial Regex WindowsAbsolutePath();

    /// <summary>Keeps the log bounded: when the cap is hit, the current file becomes the single .old.</summary>
    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var old = _path + ".old";
        File.Delete(old);
        File.Move(_path, old);
    }
}
