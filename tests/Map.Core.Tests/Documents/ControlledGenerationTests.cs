using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class ControlledGenerationTests
{
    private static MapDocument Document() => HexMapFactory.Create("R20", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 32, 24, "#FFFFFFFF");
    private static HexGenerationRecipe Recipe => new() { Settings = new() { Seed = 17 }, Steps = [HexGenerationStep.Land] };

    [Fact]
    public void Protection_and_selection_preserve_cells_and_intersecting_paths()
    {
        var layer = Document().Layers.OfType<HexLayer>().Single();
        var h = layer.Board.Layout.FromOffset(new(4, 4)); var outside = layer.Board.Layout.FromOffset(new(20, 20));
        var point = HexCell.Of("starter/grass", feature: "starter/village").WithPointsOfInterest([new("starter/village", "Дом"), new("starter/ruins", "Руины")]);
        var path = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [h, h.Neighbors().First()], []);
        layer = layer with { Board = layer.Board.With([new(h, point), new(outside, point)]), GenerationProtection = [h], Paths = [path] };
        var selected = h.Neighbors().First();
        var after = ControlledHexGeneration.Run(layer, Recipe with { Selection = [h, selected] });
        Assert.Same(point, after.Board[h]); Assert.Same(point, after.Board[outside]);
        Assert.NotNull(after.Board[selected]); Assert.Equal(path, Assert.Single(after.Paths));
        Assert.Equal(2, after.Board[h]!.PointsOfInterest.Count);
    }

    [Fact]
    public void Local_calculation_keeps_the_draw_order_of_unchanged_paths()
    {
        var layer = Document().Layers.OfType<HexLayer>().Single();
        var h = layer.Board.Layout.FromOffset(new(4, 4)); var next = h.Neighbors().First();
        var inside = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [h, next], []);
        var outside = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Centers, [next, next.Neighbors().First()], []);
        layer = layer with { Paths = [inside, outside] };
        var after = ControlledHexGeneration.Run(layer, Recipe with { Steps = [HexGenerationStep.Relief], Selection = [h, next] });
        Assert.Equal(layer.Paths, after.Paths);
    }

    [Fact]
    public async Task Cancellation_of_running_calculation_never_changes_document()
    {
        var session = new EditingSession(Document()); var original = session.Document;
        using var job = new BackgroundGeneration();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = job.CalculateAsync(session, (snapshot, token) =>
        {
            started.SetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30)); job.Cancel(); await work;
        Assert.Same(original, session.Document); Assert.Equal(0, session.UndoDepth); Assert.Null(job.Preview);
        Assert.Equal(GenerationApplyResult.MissingPreview, job.TryApply(session));
    }

    [Fact]
    public async Task Revision_change_during_calculation_requires_recalculation()
    {
        var session = new EditingSession(Document()); using var job = new BackgroundGeneration();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var work = job.CalculateAsync(session, (snapshot, token) =>
        {
            started.SetResult(); finish.Wait(token); return Command(snapshot, token);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var layer = session.Document.Layers.OfType<HexLayer>().Single();
        session.Execute(new HexLayerEditCommand("Edit", layer, layer with { FogEnabled = true }));
        finish.Set(); await work;
        var edited = session.Document;
        Assert.True(job.Conflicted); Assert.Equal(GenerationApplyResult.Conflict, job.TryApply(session));
        Assert.Same(edited, session.Document); Assert.Equal(1, session.UndoDepth);
    }

    [Fact]
    public async Task Preview_is_read_only_and_one_undo_reverts_apply_including_recipe_and_reasons()
    {
        var session = new EditingSession(Document()); var original = session.Document;
        using var job = new BackgroundGeneration(); await job.CalculateAsync(session, Command);
        Assert.Same(original, session.Document); Assert.Equal(0, session.UndoDepth); Assert.NotNull(job.Preview);
        Assert.Equal(GenerationApplyResult.Applied, job.TryApply(session)); Assert.Equal(1, session.UndoDepth);
        var generated = session.Document.Layers.OfType<HexLayer>().Single();
        Assert.NotNull(generated.GenerationRecipe); Assert.All(generated.Board.Cells, p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GenerationReason)));
        session.Undo(); Assert.Equal(original.Layers, session.Document.Layers);
        session.Redo(); Assert.Equal(generated, session.Document.Layers.OfType<HexLayer>().Single());
    }

    [Fact]
    public void Generator_honours_pre_cancelled_token()
    {
        var layer = Document().Layers.OfType<HexLayer>().Single(); using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => ControlledHexGeneration.Run(layer, Recipe, source.Token));
        Assert.Equal(0, layer.Board.CellCount);
    }

    [Fact]
    public void All_steps_are_repeatable_and_generated_paths_have_reasons()
    {
        var layer = Document().Layers.OfType<HexLayer>().Single();
        var recipe = Recipe with { Steps = [.. Enum.GetValues<HexGenerationStep>()], Settings = new() { Seed = 5, SettlementsPerThousand = 80 } };
        var a = ControlledHexGeneration.Run(layer, recipe); var b = ControlledHexGeneration.Run(layer, recipe);
        Assert.Equal(a.Board.Cells.OrderBy(p => p.Key.R).ThenBy(p => p.Key.Q), b.Board.Cells.OrderBy(p => p.Key.R).ThenBy(p => p.Key.Q));
        Assert.NotEmpty(a.Paths); Assert.All(a.Paths, p => Assert.False(string.IsNullOrWhiteSpace(p.GenerationReason)));
    }

    private static IEditCommand Command(MapDocument snapshot, CancellationToken token)
    {
        var layer = snapshot.Layers.OfType<HexLayer>().Single();
        return new HexLayerEditCommand("Apply", layer, ControlledHexGeneration.Run(layer, Recipe, token));
    }
}
