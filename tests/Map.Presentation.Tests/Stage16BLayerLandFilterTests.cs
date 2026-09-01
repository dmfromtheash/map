using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Map.Presentation.Tests;

public sealed class Stage16BLayerLandFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-stage16b", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Mask_copy_is_independent_and_undoable()
    {
        var (editor, ids) = Editor();
        editor.SelectLayer(ids.Brush);
        editor.ToggleActiveMask();
        editor.ActiveTool = EditorTool.Mask;
        editor.BrushSettings = new BrushSettings { Radius = 18, Softness = .4, Opacity = .8 };
        Assert.True(editor.BeginStroke(80, 80, .7));
        editor.ContinueStroke(120, 100, .9);
        editor.EndStroke();
        var sourceBefore = (BrushLayer)editor.Session.Document.FindLayer(ids.Brush)!;
        var sourceBytes = (byte[])sourceBefore.Mask!.Tiles.TryGet(sourceBefore.Mask.Tiles.Keys.Single())!.Clone();

        editor.CopyActiveMask();
        editor.SelectLayer(ids.Objects);
        Assert.True(editor.PasteCopiedMask());
        var target = (ObjectLayer)editor.Session.Document.FindLayer(ids.Objects)!;

        Assert.NotSame(sourceBefore.Mask, target.Mask);
        Assert.Equal(sourceBytes, target.Mask!.Tiles.TryGet(target.Mask.Tiles.Keys.Single()));
        Assert.Equal(sourceBytes, ((BrushLayer)editor.Session.Document.FindLayer(ids.Brush)!).Mask!.Tiles
            .TryGet(sourceBefore.Mask.Tiles.Keys.Single()));
        Assert.True(editor.Session.Undo());
        Assert.Null(((ObjectLayer)editor.Session.Document.FindLayer(ids.Objects)!).Mask);
        Assert.True(editor.Session.Redo());
        Assert.NotNull(((ObjectLayer)editor.Session.Document.FindLayer(ids.Objects)!).Mask);
    }

    [Fact]
    public void Exact_layer_land_and_filter_values_survive_save_reopen()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stage16b.mapproj");
        var (editor, ids) = Editor();

        editor.SelectLayer(ids.Objects);
        Assert.True(editor.SetActiveLayerOpacity(63));
        Assert.True(editor.SetActiveLayerBlend("Overlay"));
        Assert.True(editor.SetActiveObjectLayerEffects(new LayerEffects
        {
            ClippingEnabled = true, ShadowColor = "#FF213547", OuterShadowOpacity = .42,
            InnerShadowOpacity = .18, ShadowOffsetX = 7, ShadowOffsetY = -3, ShadowBlur = 11,
        }));
        Assert.True(editor.SetActiveObjectSublayerState(2, visible: false, locked: true));

        editor.SelectLayer(ids.Land);
        Assert.True(editor.ApplyLandShape(LandShapeKind.Rectangle, new WorldBounds(40, 50, 220, 180)));
        Assert.True(editor.MoveActiveLandArea(new WorldBounds(40, 50, 220, 180), 32, 16, snapToGrid: false));
        Assert.True(editor.SetActiveLandStyle(style => style with
        {
            FillColor = "#FF779955", OutlineColor = "#FF223311", OutlineWidth = 5,
            InnerShadowOpacity = .2, OuterShadowOpacity = .35,
            ShadowOffsetX = 4, ShadowOffsetY = 6, ShadowBlur = 9,
            RippleColor = "#665577AA", RippleWidth = 3, RippleSpacing = 14, RippleCount = 4,
        }));

        editor.SelectLayer(ids.Filter);
        Assert.True(editor.SetActiveFilter(.12, -.2, .34, 27, -.15, .44, 2.5));

        MapProjectStore.Save(editor.Session.Document, path,
            new SaveMetadata("stage16", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(editor.Session.Document, 96, 64));
        var reopened = MapProjectStore.Open(path).Document;

        var objects = (ObjectLayer)reopened.FindLayer(ids.Objects)!;
        Assert.Equal(.63, objects.Opacity, 6);
        Assert.Equal("Overlay", objects.Blend);
        Assert.Equal(new LayerEffects
        {
            ClippingEnabled = true, ShadowColor = "#FF213547", OuterShadowOpacity = .42,
            InnerShadowOpacity = .18, ShadowOffsetX = 7, ShadowOffsetY = -3, ShadowBlur = 11,
        }, objects.Effects);
        Assert.Equal(new ObjectSublayerState(2, false, true), objects.StateFor(2));
        var land = (LandLayer)reopened.FindLayer(ids.Land)!;
        Assert.True(land.GeometryTiles.TileCount > 0);
        Assert.Equal(4, land.EdgeStyle.RippleCount);
        Assert.Equal(4, land.EdgeStyle.ShadowOffsetX);
        var filter = (FilterLayer)reopened.FindLayer(ids.Filter)!;
        Assert.Equal(.12, filter.Settings.Brightness, 6);
        Assert.Equal(27, filter.Settings.HueDegrees, 6);
        Assert.Equal(2.5, filter.Settings.TextureScale, 6);
        Assert.Equal(MapProjectFormat.SchemaVersion, MapProjectStore.Inspect(path).SourceSchema);
    }

    [Fact]
    public void V2_migrates_copy_only_through_the_adjacent_v3_step()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source-v2.mapproj");
        var destination = Path.Combine(_root, "copy-v3.mapproj");
        var (editor, _) = Editor();
        MapProjectStore.Save(editor.Session.Document, source,
            new SaveMetadata("stage16", DateTimeOffset.UnixEpoch),
            ThumbnailRenderer.RenderPng(editor.Session.Document, 64, 48));
        DowngradeRootManifest(source);
        var original = File.ReadAllBytes(source);

        var inspection = MapProjectStore.Inspect(source);
        Assert.Equal(ProjectFileStatus.MigrationRequired, inspection.Status);
        Assert.Equal(2, inspection.SourceSchema);
        Assert.Equal(3, inspection.TargetSchema);
        var migrated = MapProjectStore.MigrateCopy(source, destination);

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(3, migrated.TargetSchema);
        Assert.Equal(ProjectFileStatus.Ready, MapProjectStore.Inspect(destination).Status);
        Assert.Equal(editor.Session.Document.Id, migrated.Project.Document.Id);
    }

    private static (EditorViewModel Editor, (LayerId Objects, LayerId Brush, LayerId Land, LayerId Filter) Ids) Editor()
    {
        var objects = new ObjectLayer(LayerId.New(), "objects", true, false, 1, []);
        var brush = BrushLayer.CreateEmpty(LayerId.New(), "brush");
        var land = LandLayer.CreateEmpty(LayerId.New(), "land");
        var filter = new FilterLayer(LayerId.New(), "filter", true, false, 1, new FilterSettings());
        var document = new MapDocument(DocumentId.New(), "stage16b", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"), objects, brush, land, filter]);
        var editor = new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        return (editor, (objects.Id, brush.Id, land.Id, filter.Id));
    }

    private static void DowngradeRootManifest(string path)
    {
        var entries = new List<(string Name, byte[] Bytes, DateTimeOffset Time)>();
        using (var source = ZipFile.OpenRead(path))
        {
            foreach (var entry in source.Entries)
            {
                using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory);
                var bytes = memory.ToArray();
                if (entry.FullName == MapProjectFormat.ManifestEntry)
                {
                    var json = JsonNode.Parse(bytes)!.AsObject(); json["schemaVersion"] = 2;
                    bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json);
                }
                entries.Add((entry.FullName, bytes, entry.LastWriteTime));
            }
        }
        var temp = path + ".downgrade";
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = item.Time;
                using var stream = entry.Open(); stream.Write(item.Bytes);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
