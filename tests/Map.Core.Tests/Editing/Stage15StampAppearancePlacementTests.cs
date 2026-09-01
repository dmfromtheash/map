using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage15StampAppearancePlacementTests
{
    [Fact]
    public void Random_angle_is_deterministic_and_uses_only_the_selected_step()
    {
        var request = new StampPlacementRequest
        {
            CenterX = 400,
            CenterY = 300,
            AreaRadius = 180,
            Density = 80,
            BaseWidth = 64,
            Seed = 1520,
            RandomAngleStep = StampRandomAngleStep.Degrees45,
            RandomFlip = true,
        };
        var variants = new[] { new StampVariantSource(new string('a', 64), 120, 80) };

        var first = StampPlacement.PlanArea(request, variants);
        var replay = StampPlacement.PlanArea(request, variants);

        Assert.Equal(first, replay);
        Assert.All(first, item => Assert.Equal(0, item.RotationDegrees % 45));
        Assert.Contains(first, item => item.FlipHorizontal || item.FlipVertical);
    }

    [Fact]
    public void Appearance_contract_contains_every_stage15_key_and_canonicalises_shadow_colour()
    {
        var required = new[]
        {
            "Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "SoftLight",
            "HardLight", "Difference", "Add", "ColorBurn", "ColorDodge", "Exclusion",
            "Hue", "Saturation", "Color", "Luminosity",
        };

        Assert.Equal(required, BlendModes.Supported);
        Assert.Equal("#FF12AB34", AppearanceColor.Normalize("#12ab34"));
        Assert.Equal("#8012AB34", AppearanceColor.Normalize("8012ab34"));
        Assert.True(AppearanceColor.IsCanonical("#8012AB34"));
        Assert.False(AppearanceColor.IsCanonical("#8012ab34"));
    }

    [Fact]
    public void Replacement_preserves_scale_and_effects_but_adopts_new_asset_aspect()
    {
        var layerId = LayerId.New();
        var original = Asset(new string('a', 64), "old");
        var replacement = Asset(new string('b', 64), "wide");
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "old", 20, 40, 200, 100, 33,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Sublayer = 4,
            ObjectOpacity = 0.7,
            Stamp = new StampStyle(original.Sha256)
            {
                ReferenceWidth = 100,
                ReferenceHeight = 50,
                Blend = "ColorDodge",
                Blur = 3,
                FlipHorizontal = true,
            },
        };
        var document = new MapDocument(
            DocumentId.New(), "stage15", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(layerId, "objects", true, false, 1, [stamp]),
            ],
            [original]);
        var session = new EditingSession(document);

        session.Execute(new ReplaceStampAssetCommand(stamp, replacement, replacementAspectRatio: 2));

        var changed = ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Single();
        Assert.Equal(200, changed.Width);
        Assert.Equal(400, changed.Height);
        Assert.Equal(100, changed.Stamp!.ReferenceWidth);
        Assert.Equal(200, changed.Stamp.ReferenceHeight);
        Assert.Equal("ColorDodge", changed.Stamp.Blend);
        Assert.True(changed.Stamp.FlipHorizontal);
        Assert.Equal(4, changed.Sublayer);
        Assert.Equal(0.7, changed.ObjectOpacity);
        Assert.True(session.Undo());
        Assert.Equal(stamp, ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Single());
    }

    private static MapAssetReference Asset(string hash, string name) => new(
        hash, name, "image/png", "png", ProjectAssetStorage.Embedded,
        DateTimeOffset.UnixEpoch, "test", "test", [1, 2, 3]);
}
