using System.Diagnostics;
using Map.Spike.Persistence;
using Map.Spike.Persistence.Atomic;
using Map.Spike.Persistence.Recovery;
using Map.Spike.Tests.Support;

namespace Map.Spike.Tests.Persistence;

/// <summary>
/// The in-process fault tests cover the code path. This one covers the real thing: a separate
/// process is killed mid-save, so no finally block, finalizer or flush-on-exit can run, and we
/// then inspect what actually survived on disk.
/// </summary>
[Collection("process-kill")]
public class ProcessKillRecoveryTests
{
    [Fact]
    public void Killing_the_process_before_replace_preserves_the_last_good_file_and_leaves_recoverable_work()
    {
        using var workspace = new TempWorkspace();

        var targetPath = workspace.File("killed" + SpikeFormat.Extension);
        var helper = ResolveHelperAssembly();

        // First run: writes a clean baseline file, then dies before the replace.
        var (ExitCode, StdOut, StdErr) = RunHelper(helper, targetPath, workspace.JournalRoot, nameof(SavePhase.DuringReplace));

        Assert.NotEqual(0, ExitCode);
        Assert.Contains("crash-phase=DuringReplace", StdOut);

        var pendingRevision = long.Parse(
            StdOut.Split('\n')
                .First(l => l.StartsWith("pending-revision=", StringComparison.Ordinal))
                .Split('=')[1].Trim());

        // 1. The baseline file survived the kill and still opens.
        Assert.True(File.Exists(targetPath), "The previously saved file did not survive the crash.");
        var survivor = AtomicDocumentStore.Load(targetPath);

        // 2. It is the state BEFORE the interrupted save, not a half-written mix.
        Assert.True(
            survivor.Revision < pendingRevision,
            $"Target revision {survivor.Revision} should predate the interrupted revision {pendingRevision}.");

        // 3. The interrupted work is recoverable, and the journal says so explicitly.
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var record = journal.TryRead(survivor.DocumentId);

        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.True(record.HasRecoverableWork, "The journal did not offer the durable temp file.");
        Assert.Equal(pendingRevision, record.PendingRevision);

        // 4. And the recovered file is a complete, loadable document with the newer work in it.
        var recovered = AtomicDocumentStore.Load(record.TempPath);
        Assert.Equal(pendingRevision, recovered.Revision);
        Assert.Equal(survivor.DocumentId, recovered.DocumentId);
        Assert.True(
            recovered.Brush.TileCount > survivor.Brush.TileCount,
            "The recovered document does not contain the stroke made before the crash.");
    }

    [Fact]
    public void Killing_the_process_while_the_temp_is_still_being_written_leaves_nothing_claiming_to_be_recoverable()
    {
        using var workspace = new TempWorkspace();

        var targetPath = workspace.File("killed-early" + SpikeFormat.Extension);
        var helper = ResolveHelperAssembly();

        var (ExitCode, StdOut, StdErr) = RunHelper(helper, targetPath, workspace.JournalRoot, nameof(SavePhase.MidTempWrite));
        Assert.NotEqual(0, ExitCode);

        Assert.True(File.Exists(targetPath));
        var survivor = AtomicDocumentStore.Load(targetPath);

        var journal = new RecoveryJournal(workspace.JournalRoot);
        var record = journal.TryRead(survivor.DocumentId);

        Assert.NotNull(record);
        Assert.Equal(RecoveryState.Started, record!.StateValue);

        // A partial temp file may well be sitting on disk. It must never be advertised as the
        // user's recovered work.
        Assert.False(record.HasRecoverableWork);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunHelper(
        string helperAssembly, string targetPath, string journalRoot, string phase)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(helperAssembly);
        startInfo.ArgumentList.Add("crash-during-save");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add("--journal");
        startInfo.ArgumentList.Add(journalRoot);
        startInfo.ArgumentList.Add("--phase");
        startInfo.ArgumentList.Add(phase);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the crash helper.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(120_000), "The crash helper did not exit in time.");
        return (process.ExitCode, stdout, stderr);
    }

    private static string DotnetPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static string ResolveHelperAssembly()
    {
        // tests/<project>/bin/<cfg>/<tfm>  ->  repo spike root
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = current.Parent!.Name;
        var tfm = current.Name;

        while (current is not null && current.Name != "spike")
        {
            current = current.Parent;
        }

        Assert.NotNull(current);

        var helper = Path.Combine(
            current!.FullName, "src", "Map.Spike.Benchmarks", "bin", configuration, tfm,
            "Map.Spike.Benchmarks.dll");

        Assert.True(
            File.Exists(helper),
            $"The crash helper is not built at {helper}. Build the whole solution before running tests.");

        return helper;
    }
}
