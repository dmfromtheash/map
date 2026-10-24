using System.Runtime.CompilerServices;
using Map.Core.Resources;

namespace Map.Tests.Shared;

/// <summary>
/// Every test process works with the same predictable machine: 16 GiB, 8 GiB free, 512 MiB held
/// by Map. Without it WorkingMemoryBudget.Shared read the real free memory of Windows, and with
/// about 2 GB free the memory guard refused 1-19 UI tests («Недостаточно свободной памяти…»).
/// The guard's own refusals are tested on budgets constructed for that purpose.
/// Linked into each test project that references Map.Core.
/// </summary>
internal static class FixedWorkingMemory
{
    public const long PhysicalBytes = 16L * 1024 * WorkingMemoryBudget.MiB;
    public const long AvailableBytes = 8L * 1024 * WorkingMemoryBudget.MiB;
    public const long MapPrivateBytes = 512 * WorkingMemoryBudget.MiB;

#pragma warning disable CA2255 // A module initializer is exactly what a test process needs here.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Pin() => WorkingMemoryBudget.UseFixedSharedMemoryForTests(
        new WorkingMemorySnapshot(PhysicalBytes, AvailableBytes, MapPrivateBytes));
}
