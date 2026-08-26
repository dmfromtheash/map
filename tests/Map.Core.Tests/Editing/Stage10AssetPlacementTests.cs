using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// The measurable placement semantics of the stamp tool: Area is a radius in world units, Density
/// is stamps per 10 000 square world units, and one gesture resolves a complete immutable plan
/// before any command exists. Replaying the same seed must reproduce the plan exactly.
/// </summary>
public class Stage10AssetPlacementTests
{
    private static readonly StampVariantSource[] Twelve =
        [.. Enumerable.Range(0, 12).Select(index => new StampVariantSource(Sha(index), 512, 512))];

    [Theory]
    [InlineData(0, 100, 1)]          // an empty density still places the one stamp the user asked for
    [InlineData(100, 100, 314)]      // round(100 * PI * 100^2 / 10000)
    [InlineData(25, 40, 13)]         // round(25 * PI * 1600 / 10000)
    [InlineData(1_000_000, 500, 5_000)]
    public void Count_follows_the_documented_density_formula_and_stays_inside_its_bounds(
        double density, double radius, int expected)
    {
        Assert.Equal(expected, StampPlacement.CountFor(density, radius));
    }

    [Fact]
    public void Area_radius_is_reported_in_grid_cells_when_a_grid_is_active()
    {
        Assert.Equal(4.0, StampPlacement.RadiusInCells(radius: 128, cellSize: 32));
        Assert.Null(StampPlacement.RadiusInCells(radius: 128, cellSize: 0));
    }

    [Fact]
    public void The_same_seed_reproduces_an_identical_plan_and_a_different_seed_does_not()
    {
        var request = Request(seed: 4_242);

        var first = StampPlacement.PlanArea(request, Twelve);
        var second = StampPlacement.PlanArea(request, Twelve);
        var other = StampPlacement.PlanArea(request with { Seed = 4_243 }, Twelve);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.NotEmpty(first);
    }

    [Fact]
    public void Every_sampled_point_lands_inside_the_requested_circle()
    {
        var request = Request(seed: 7) with { CenterX = 900, CenterY = -250, AreaRadius = 180 };

        var plan = StampPlacement.PlanArea(request, Twelve);

        Assert.True(plan.Count > 30, $"Only {plan.Count} stamps were planned.");
        foreach (var item in plan)
        {
            var dx = item.X + (item.Width / 2) - request.CenterX;
            var dy = item.Y + (item.Height / 2) - request.CenterY;
            Assert.True(
                Math.Sqrt((dx * dx) + (dy * dy)) <= request.AreaRadius + 1e-9,
                $"Stamp at ({item.X}, {item.Y}) fell outside the area.");
        }
    }

    [Fact]
    public void A_gesture_without_randomisation_uses_only_the_current_variant_and_no_transform_noise()
    {
        var request = Request(seed: 11) with
        {
            RandomizeVariant = false,
            RandomAngle = false,
            RandomFlip = false,
        };

        var plan = StampPlacement.PlanArea(request, Twelve);

        Assert.All(plan, item => Assert.Equal(Twelve[0].Sha256, item.Sha256));
        Assert.All(plan, item => Assert.Equal(0, item.RotationDegrees));
        Assert.All(plan, item => Assert.False(item.FlipHorizontal));
        Assert.All(plan, item => Assert.False(item.FlipVertical));
    }

    [Fact]
    public void A_randomised_gesture_draws_several_different_variants_from_the_set()
    {
        var plan = StampPlacement.PlanArea(Request(seed: 99), Twelve);

        var distinct = plan.Select(item => item.Sha256).Distinct().Count();
        Assert.True(distinct >= 3, $"Only {distinct} distinct variants were used.");
        Assert.True(distinct <= Twelve.Length);
        Assert.Contains(plan, item => item.RotationDegrees != 0);
    }

    [Fact]
    public void Each_variant_keeps_its_own_aspect_ratio_at_the_requested_base_width()
    {
        var variants = new[]
        {
            new StampVariantSource(Sha(0), 512, 512),
            new StampVariantSource(Sha(1), 512, 256),
        };
        var request = Request(seed: 3) with { BaseWidth = 120 };

        var plan = StampPlacement.PlanArea(request, variants);

        foreach (var item in plan)
        {
            var expected = item.Sha256 == Sha(0) ? 120.0 : 60.0;
            Assert.Equal(120.0, item.Width, 6);
            Assert.Equal(expected, item.Height, 6);
        }
    }

    [Fact]
    public void One_placement_puts_exactly_the_current_variant_at_the_pointer()
    {
        var request = Request(seed: 5) with { CenterX = 400, CenterY = 300, BaseWidth = 64 };

        var plan = StampPlacement.PlanOne(request, new StampVariantSource(Sha(6), 512, 256));

        var item = Assert.Single(plan);
        Assert.Equal(Sha(6), item.Sha256);
        Assert.Equal(400 - 32, item.X, 6);
        Assert.Equal(300 - 16, item.Y, 6);
        Assert.Equal(64, item.Width, 6);
        Assert.Equal(32, item.Height, 6);
        Assert.Equal(0, item.RotationDegrees);
    }

    [Fact]
    public void An_empty_variant_list_is_rejected_rather_than_planning_nothing_silently()
    {
        Assert.Throws<ArgumentException>(() => StampPlacement.PlanArea(Request(seed: 1), []));
    }

    [Fact]
    public void The_random_variant_after_a_placement_advances_deterministically_from_the_same_seed()
    {
        var first = StampPlacement.NextVariantIndex(seed: 500, currentIndex: 0, variantCount: 12);
        var again = StampPlacement.NextVariantIndex(seed: 500, currentIndex: 0, variantCount: 12);

        Assert.Equal(first, again);
        Assert.InRange(first, 0, 11);
        Assert.Equal(0, StampPlacement.NextVariantIndex(seed: 500, currentIndex: 0, variantCount: 1));

        // Whatever the seed, the tool must actually move off the variant the user just placed.
        for (var seed = 0; seed < 200; seed++)
        {
            for (var current = 0; current < 12; current++)
            {
                var next = StampPlacement.NextVariantIndex(seed, current, 12);
                Assert.InRange(next, 0, 11);
                Assert.NotEqual(current, next);
            }
        }
    }

    private static StampPlacementRequest Request(int seed) => new()
    {
        CenterX = 500,
        CenterY = 500,
        AreaRadius = 150,
        Density = 60,
        Seed = seed,
        RandomizeVariant = true,
        RandomAngle = true,
        RandomFlip = true,
        BaseWidth = 96,
    };

    private static string Sha(int index) => index.ToString("x64");
}

/// <summary>
/// One scatter gesture across several variants is one history entry. Undo must withdraw exactly
/// the references this command introduced, and redo must return the same plan without rerolling.
/// </summary>
public class Stage10MultiAssetCommandTests
{
    [Fact]
    public void One_gesture_over_three_variants_is_a_single_undoable_entry()
    {
        var document = Fixture(out var layerId);
        var session = new EditingSession(document);
        var references = References(3);
        var objects = Stamps(references);

        session.Execute(new PlaceAssetObjectsCommand(layerId, references, objects));

        Assert.Equal(3, Objects(session.Document, layerId).Count);
        Assert.Equal(3, session.Document.Assets.Count);

        session.Undo();
        Assert.Empty(Objects(session.Document, layerId));
        Assert.Empty(session.Document.Assets);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Redo_restores_the_same_object_ids_positions_transforms_and_hashes()
    {
        var document = Fixture(out var layerId);
        var session = new EditingSession(document);
        var references = References(4);
        var objects = Stamps(references);
        session.Execute(new PlaceAssetObjectsCommand(layerId, references, objects));
        var before = Objects(session.Document, layerId);

        session.Undo();
        session.Redo();

        var after = Objects(session.Document, layerId);
        Assert.Equal(before.Count, after.Count);
        for (var index = 0; index < before.Count; index++)
        {
            Assert.Equal(before[index].Id, after[index].Id);
            Assert.Equal(before[index].X, after[index].X);
            Assert.Equal(before[index].Y, after[index].Y);
            Assert.Equal(before[index].Width, after[index].Width);
            Assert.Equal(before[index].Height, after[index].Height);
            Assert.Equal(before[index].RotationDegrees, after[index].RotationDegrees);
            Assert.Equal(before[index].Stamp!.AssetSha256, after[index].Stamp!.AssetSha256);
            Assert.Equal(before[index].Stamp!.FlipHorizontal, after[index].Stamp!.FlipHorizontal);
            Assert.Equal(before[index].Stamp!.FlipVertical, after[index].Stamp!.FlipVertical);
        }

        Assert.Equal(
            [.. before.Select(item => item.Stamp!.AssetSha256)],
            [.. after.Select(item => item.Stamp!.AssetSha256)]);
    }

    [Fact]
    public void Undo_keeps_a_reference_that_the_document_already_owned()
    {
        var references = References(2);
        var document = Fixture(out var layerId).WithAssets([references[0]]);
        var session = new EditingSession(document);

        session.Execute(new PlaceAssetObjectsCommand(layerId, references, Stamps(references)));
        Assert.Equal(2, session.Document.Assets.Count);

        session.Undo();

        var kept = Assert.Single(session.Document.Assets);
        Assert.Equal(references[0].Sha256, kept.Sha256);
    }

    [Fact]
    public void Undo_keeps_a_reference_that_another_object_still_uses()
    {
        var document = Fixture(out var layerId);
        var session = new EditingSession(document);
        var shared = References(1);
        session.Execute(new PlaceAssetObjectsCommand(layerId, shared, Stamps(shared)));

        var second = References(2);
        session.Execute(new PlaceAssetObjectsCommand(layerId, second, Stamps(second)));
        Assert.Equal(2, session.Document.Assets.Count);

        session.Undo();

        var kept = Assert.Single(session.Document.Assets);
        Assert.Equal(shared[0].Sha256, kept.Sha256);
        Assert.Single(Objects(session.Document, layerId));
    }

    [Fact]
    public void Every_placed_object_must_reference_one_of_the_supplied_assets()
    {
        var document = Fixture(out var layerId);
        var references = References(2);
        var stranger = Stamps(References(3))[2];

        Assert.Throws<ArgumentException>(
            () => new PlaceAssetObjectsCommand(layerId, references, [.. Stamps(references), stranger]));
        Assert.Throws<ArgumentException>(
            () => new PlaceAssetObjectsCommand(layerId, references, []));
        Assert.NotNull(document);
    }

    [Fact]
    public void The_history_entry_names_how_many_stamps_and_how_many_variants_the_gesture_placed()
    {
        var references = References(3);
        var command = new PlaceAssetObjectsCommand(LayerId.New(), references, Stamps(references));

        Assert.Contains("3", command.Name);
        Assert.Equal(3, command.Count);
        Assert.Equal(3, command.VariantCount);
    }

    // ---------------------------------------------------------------- helpers

    private static MapDocument Fixture(out LayerId layerId)
    {
        layerId = LayerId.New();
        return new MapDocument(
            DocumentId.New(), "scatter", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4_096, 4_096, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(layerId, "objects", true, false, 1, []),
            ]);
    }

    private static IReadOnlyList<SceneObject> Objects(MapDocument document, LayerId layerId) =>
        ((ObjectLayer)document.FindLayer(layerId)!).Objects;

    private static MapAssetReference[] References(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index => new MapAssetReference(
            index.ToString("x64"),
            $"variant-{index:D2}",
            "image/png",
            "png",
            ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch,
            "diagnostic fixture",
            "CC0-1.0",
            embeddedContent: [(byte)index, 1, 2, 3])),
    ];

    private static SceneObject[] Stamps(IReadOnlyList<MapAssetReference> references) =>
    [
        .. references.Select((reference, index) => new SceneObject(
            ObjectId.New(),
            SceneObjectKind.Stamp,
            reference.DisplayName,
            100 + (index * 40),
            200 + (index * 25),
            64,
            48,
            index * 7,
            "#FFFFFFFF",
            "#00000000",
            0,
            true,
            false)
        {
            Stamp = new StampStyle(reference.Sha256) { FlipHorizontal = index % 2 == 0 },
        }),
    ];
}
