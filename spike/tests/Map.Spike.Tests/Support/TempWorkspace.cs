namespace Map.Spike.Tests.Support;

/// <summary>
/// Isolated directory per test. Also isolates the recovery journal root so a test never reads or
/// writes the real %LOCALAPPDATA%\Map\Recovery of the machine it runs on.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "map-spike-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(JournalRoot);
    }

    public string Root { get; }

    public string JournalRoot => Path.Combine(Root, "journal");

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not turn a passing test red; the OS reclaims it later.
        }
    }
}
