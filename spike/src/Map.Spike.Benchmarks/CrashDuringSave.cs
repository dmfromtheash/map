using Map.Spike.Core.Commands;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Persistence.Atomic;
using Map.Spike.Persistence.Recovery;

namespace Map.Spike.Benchmarks;

/// <summary>
/// Kills this process in the middle of an atomic save.
///
/// The in-process fault-injection tests prove the code path; this proves the outcome survives an
/// actual process death, with no finally blocks, no flush on exit and no chance for managed code
/// to tidy up after itself. The caller inspects what is left on disk.
/// </summary>
public static class CrashDuringSave
{
    public static int Run(string[] args)
    {
        var targetPath = ArgValue(args, "--target") ?? throw new ArgumentException("--target is required.");
        var journalRoot = ArgValue(args, "--journal") ?? throw new ArgumentException("--journal is required.");
        var phaseText = ArgValue(args, "--phase") ?? nameof(SavePhase.DuringReplace);
        var seedText = ArgValue(args, "--seed");
        var phase = Enum.Parse<SavePhase>(phaseText);

        var journal = new RecoveryJournal(journalRoot);
        var document = SpikeFixtures.CreateObjectScene(
            200,
            canvasSize: 1024,
            seed: seedText is null ? SpikeFixtures.DefaultSeed : ulong.Parse(seedText));

        // Establish a known-good previous file first, so the crash has something to preserve.
        if (!File.Exists(targetPath))
        {
            new AtomicDocumentStore(journal).Save(document, targetPath);
        }

        // Now make an edit that must NOT appear in the target after the crash.
        var stroke = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        stroke.AddPoint(new Vec2(400, 400));
        stroke.AddPoint(new Vec2(520, 470));
        new CommandHistory().Execute(document, stroke.Complete()!);

        Console.WriteLine($"pending-revision={document.Revision}");
        Console.WriteLine($"crash-phase={phase}");
        Console.Out.Flush();

        var store = new AtomicDocumentStore(journal, new HardKillInjector(phase));
        store.Save(document, targetPath);

        // Unreachable: the injector terminates the process.
        Console.WriteLine("ERROR: the process was not killed.");
        return 2;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private sealed class HardKillInjector(SavePhase killAt) : ISaveFaultInjector
    {
        private readonly SavePhase _killAt = killAt;

        public void OnPhase(SavePhase phase)
        {
            if (phase != _killAt)
            {
                return;
            }

            // Kill, not Environment.Exit: no finalizers, no finally blocks, no flush-on-exit.
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            self.Kill();
            Thread.Sleep(30_000);
        }
    }
}
