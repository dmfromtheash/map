using System.Diagnostics;
using Map.Core.Documents;
using Map.Persistence.Recovery;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// The Stage 2 gate, with a real process death: no finally block, finalizer or flush-on-exit may
/// tidy up. The child establishes a last-good target, starts saving a changed document, signals
/// after the journal records the durable temp, and blocks until this test kills it.
/// </summary>
public class ProcessKillGateTests
{
    private static readonly Guid KnownDocumentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Killing_the_process_between_durable_temp_and_replace_preserves_old_and_offers_new()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.File("killed" + MapProjectFormat.Extension);
        var journalRoot = Path.Combine(workspace.Root, "journal");

        var (ExitCode, StdOut) = RunKillHost(targetPath, journalRoot);
        Assert.NotEqual(0, ExitCode);
        Assert.Contains("BASELINE", StdOut);
        Assert.Contains("READY", StdOut);
        Assert.DoesNotContain("ERROR-NOT-KILLED", StdOut);

        // 1. The old target survived the kill, opens, and is semantically the old version.
        var survivor = MapProjectStore.Open(targetPath);
        Assert.Equal("old", survivor.Document.Title);
        Assert.Equal(100, survivor.Document.Layers.OfType<ObjectLayer>().Single().Objects[0].X);

        // 2. The journal reads without crashing and records the durable temp.
        var journal = new RecoveryJournal(journalRoot);
        var record = journal.TryRead(KnownDocumentId);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.True(record.HasRecoverableWork);

        // 3. The candidate itself opens and is semantically the NEW version.
        var candidateProject = MapProjectStore.Open(record.TempPath);
        Assert.Equal("new", candidateProject.Document.Title);
        Assert.Equal(400, candidateProject.Document.Layers.OfType<ObjectLayer>().Single().Objects[0].X);
        Assert.Equal(
            survivor.Document.Layers.OfType<ObjectLayer>().Single().Objects[0].Id,
            candidateProject.Document.Layers.OfType<ObjectLayer>().Single().Objects[0].Id); // stable ids across versions

        // 4. The recovery API surfaces the new version without destroying the old one.
        var oldBytes = File.ReadAllBytes(targetPath);
        var candidate = Assert.Single(RecoveryCandidates.Find(journal));
        Assert.Equal("new", candidate.Title);

        var restored = RecoveryCandidates.Restore(candidate);
        Assert.Equal("new", restored.Document.Title);
        Assert.Equal(oldBytes, File.ReadAllBytes(targetPath));
        Assert.Equal("old", MapProjectStore.Open(targetPath).Document.Title);
    }

    private static (int ExitCode, string StdOut) RunKillHost(string targetPath, string journalRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(ResolveKillHostAssembly());
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add(journalRoot);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the kill host.");

        // Read stdout live until READY, then kill mid-save.
        var collected = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        string? line;
        var ready = false;

        while (DateTime.UtcNow < deadline && (line = process.StandardOutput.ReadLine()) is not null)
        {
            collected.AppendLine(line);
            if (line == "READY")
            {
                ready = true;
                break;
            }
        }

        Assert.True(ready, $"The kill host never reported READY. Output so far: {collected}");

        process.Kill(); // real kill: no managed cleanup runs
        Assert.True(process.WaitForExit(60_000), "The kill host did not exit after Kill().");

        return (process.ExitCode, collected.ToString());
    }

    private static string DotnetPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static string ResolveKillHostAssembly()
    {
        // tests/<project>/bin/<cfg>/<tfm> -> repo root -> sibling test project output.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = current.Name;
        var configuration = current.Parent!.Name;

        var repoRoot = current.Parent!.Parent!.Parent!.Parent!.Parent!;
        var helper = Path.Combine(
            repoRoot.FullName, "tests", "Map.Persistence.KillHost", "bin", configuration, tfm,
            "Map.Persistence.KillHost.dll");

        Assert.True(
            File.Exists(helper),
            $"The kill host is not built at {helper}. Build the whole solution before running tests.");

        return helper;
    }
}
