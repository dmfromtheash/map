using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;

namespace Map.Core.Tests.Editing;

public sealed class WorkingMemoryBudgetTests
{
    [Fact]
    public async Task Render_caches_can_recover_after_memory_returns_without_a_new_edit()
    {
        long available = WorkingMemoryBudget.SmallSystemReserveBytes;
        var budget = new WorkingMemoryBudget(() => new(16L * 1024 * WorkingMemoryBudget.MiB,
            Interlocked.Read(ref available), WorkingMemoryBudget.MiB));
        Assert.False(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out _, out _));
        Assert.True(budget.IsUnderPressure);
        Interlocked.Exchange(ref available, 8L * 1024 * WorkingMemoryBudget.MiB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (budget.IsUnderPressure) await Task.Delay(10, timeout.Token);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.True(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out var held, out _));
        held!.Dispose();
    }

    private const long GiB = 1024 * WorkingMemoryBudget.MiB;
    private static WorkingMemorySnapshot Roomy => new(16 * GiB, 8 * GiB, GiB);

    [Theory]
    [InlineData(.5, 1)]
    [InlineData(8, 6)]
    public void Both_live_OS_reserve_and_aggregate_Map_commitment_reject_before_admission(double available, int used)
    {
        var budget = new WorkingMemoryBudget(() => new(16 * GiB, (long)(available * GiB), used * GiB));
        Assert.False(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out var lease, out var reason));
        Assert.Null(lease);
        Assert.Contains("история сохранены", reason);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task Small_edit_preview_and_save_validation_work_below_the_normal_OS_reserve()
    {
        var budget = new WorkingMemoryBudget(() => Roomy with { AvailableBytes = GiB });
        Assert.True(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out var edit, out _));
        using (edit)
        {
            Assert.True(budget.TryReserve(27 * WorkingMemoryBudget.MiB, out var preview, out _));
            preview!.Dispose();
        }
        using (await budget.EnterHeavyAsync().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.True(budget.TryReserve(27 * WorkingMemoryBudget.MiB, out var validation, out _));
            validation!.Dispose();
            Assert.True(budget.IsUnderPressure); // Caches can trim without refusing this save.
        }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void Small_allowance_cannot_be_accumulated_into_a_large_operation()
    {
        var budget = new WorkingMemoryBudget(() => Roomy with { AvailableBytes = GiB });
        Assert.False(budget.TryReserve(WorkingMemoryBudget.SmallOperationLimitBytes + 1, out _, out _));
        Assert.True(budget.TryReserve(WorkingMemoryBudget.SmallOperationLimitBytes, out var held, out _));
        using (held)
        {
            Assert.False(budget.TryReserve(1, out _, out var reason));
            Assert.Contains("Свободно в Windows: 1024 МиБ", reason);
            Assert.Contains("Для действия доступно 0 МиБ", reason);
        }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void Concurrent_reservations_cannot_spend_the_same_headroom_and_disposal_is_idempotent()
    {
        var budget = new WorkingMemoryBudget(() => Roomy);
        Assert.True(budget.TryReserve(4 * GiB, out var first, out _));
        Assert.False(budget.TryReserve(2 * GiB, out _, out _));
        first!.Dispose(); first.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
        Assert.True(budget.TryReserve(2 * GiB, out var next, out _));
        next!.Dispose();
    }

    [Fact]
    public async Task Separate_window_budgets_share_the_heavy_slot_and_cancelled_waiters_do_not_strand_it()
    {
        var first = new WorkingMemoryBudget(() => Roomy);
        var second = new WorkingMemoryBudget(() => Roomy);
        using var held = await first.EnterHeavyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = second.EnterHeavyAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, second.ReservedBytes);
        held.Dispose();
        using var next = await second.EnterHeavyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkingMemoryBudget.HeavyReserveBytes, second.ReservedBytes);
        next.Dispose();
        Assert.Equal(0, first.ReservedBytes);
        Assert.Equal(0, second.ReservedBytes);
    }

    [Fact]
    public void The_heavy_slot_is_granted_while_the_thread_pool_is_busy()
    {
        // The slot's owner waits for the whole lease. On a pool thread it queued behind busy work
        // and a 2-core CI machine timed out after 5 s; a thread of its own starts at once.
        var budget = new WorkingMemoryBudget(() => Roomy);
        // Not disposed: queued work items that start after the test still wait on it.
        var release = new ManualResetEventSlim();
        var busy = Environment.ProcessorCount * 8;
        for (var i = 0; i < busy; i++) ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(TimeSpan.FromSeconds(10)), null);
        try
        {
            var lease = budget.EnterHeavyAsync();
            // Waited synchronously: an await would need a pool thread of its own.
            Assert.True(lease.Wait(TimeSpan.FromSeconds(2)), "the slot waited for the busy pool");
            lease.Result.Dispose();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Rejected_heavy_operation_releases_the_cross_window_slot()
    {
        var memory = Roomy with { AvailableBytes = WorkingMemoryBudget.SmallSystemReserveBytes };
        var budget = new WorkingMemoryBudget(() => memory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.EnterHeavyAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        memory = Roomy;
        using var lease = await budget.EnterHeavyAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Refused_edit_preserves_document_selection_revision_and_both_history_branches()
    {
        var memory = Roomy;
        var budget = new WorkingMemoryBudget(() => memory);
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF"), layer]);
        var session = new EditingSession(document, budget);
        var first = new Rename("One"); var second = new Rename("Two");
        session.Execute(first); session.Execute(second); session.Undo();
        var before = session.Document; var revision = session.Revision;
        memory = memory with { AvailableBytes = WorkingMemoryBudget.SmallSystemReserveBytes };
        var refused = new Rename("Lost");
        session.Execute(refused);
        Assert.Same(before, session.Document);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(1, session.UndoDepth); Assert.Equal(1, session.RedoDepth);
        Assert.Equal(0, refused.ApplyCalls);
        Assert.NotNull(session.LastResourceError);
        Assert.True(session.Undo()); Assert.Same(document, session.Document);
        Assert.True(session.Redo()); Assert.Same(before, session.Document);
        memory = Roomy;
        session.Execute(new Rename("Recovered"));
        Assert.Null(session.LastResourceError);
        Assert.Equal("Recovered", session.Document.Title);
        Assert.Equal(0, budget.ReservedBytes);
    }

    private sealed class Rename(string name) : IEditCommand
    {
        private MapDocument? _before, _after;
        public int ApplyCalls { get; private set; }
        public string Name => name;
        public MapDocument Apply(MapDocument document)
        { ApplyCalls++; _before ??= document; return _after ??= document with { Title = name }; }
        public MapDocument Revert(MapDocument document) => _before!;
    }
}
