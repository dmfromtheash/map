using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Map.Persistence")]
// Test processes pin WorkingMemoryBudget.Shared to a fixed machine (tests/Shared/FixedWorkingMemory.cs).
[assembly: InternalsVisibleTo("Map.Core.Tests")]
[assembly: InternalsVisibleTo("Map.Persistence.Tests")]
[assembly: InternalsVisibleTo("Map.Persistence.KillHost")]
[assembly: InternalsVisibleTo("Map.Presentation.Tests")]
[assembly: InternalsVisibleTo("Map.Rendering.Tests")]
// MAP_PERF_FREE_MB: A/B runs of tools/Map.Performance meet the same memory pressure.
[assembly: InternalsVisibleTo("Map.Performance")]
