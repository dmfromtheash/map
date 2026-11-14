namespace Map.Rendering.Tests;

/// <summary>
/// Tests whose frames wait for the background detail-tile workers in real (wall-clock) time. The workers are
/// process-wide, ProcessorCount − 2 of them at below-normal priority, and each test's frame loop runs flat out at
/// normal priority. In parallel with the other classes on a 4-core CI runner the frame loops of four test classes
/// took every core, Windows ran the two workers only in its anti-starvation boosts, and a run that settles in
/// seconds alone did not settle in 120 s (2026-11-14: 10 tiles started, 6 adopted in 6 198 frames). The editor
/// has one frame loop that waits for vsync, so the workers keep their cores there; these tests therefore run
/// alone, after the parallel classes, and keep their real-time budgets unchanged.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTimeSettleCollection
{
    public const string Name = "Real-time settle";
}
