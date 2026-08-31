using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class TiledLayerRoundTripTests
{
    [Fact]
    public void Stage2_scene_without_layer_types_requires_copy_migration_with_stable_base()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage2.mapproj");
        var documentId = "11111111-2222-3333-4444-555555555555";
        var objectId = "01234567-89ab-cdef-0123-456789abcdef";
        var scene = Encoding.UTF8.GetBytes("""
            {
              "documentId": "11111111-2222-3333-4444-555555555555",
              "layers": [
                {
                  "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                  "name": "objects",
                  "visible": true,
                  "locked": false,
                  "opacity": 1,
                  "objects": [
                    {
                      "id": "01234567-89ab-cdef-0123-456789abcdef",
                      "kind": "Rectangle",
                      "name": "legacy",
                      "x": 10, "y": 20, "width": 30, "height": 40,
                      "rotationDegrees": 0,
                      "fillColor": "#FFFFFFFF",
                      "strokeColor": "#FF000000",
                      "strokeWidth": 0,
                      "visible": true,
                      "locked": false
                    }
                  ]
                }
              ]
            }
            """);
        var assets = Encoding.UTF8.GetBytes("{\"assets\":[]}");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = MapProjectFormat.FormatId,
            schemaVersion = 1,
            appVersion = "0.2.0",
            documentId,
            title = "legacy",
            createdUtc = "2026-08-25T12:00:00.0000000+00:00",
            modifiedUtc = "2026-08-25T12:00:00.0000000+00:00",
            canvas = new { width = 1024, height = 768, unit = "px", background = "#FF203040" },
            preset = "blank",
            sceneSha256 = Convert.ToHexStringLower(SHA256.HashData(scene)),
            assetManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(assets)),
        });

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(MapProjectFormat.ManifestEntry, manifest);
            Write(MapProjectFormat.SceneEntry, scene);
            Write(MapProjectFormat.AssetManifestEntry, assets);
            Write(MapProjectFormat.PreviewEntry, ProjectFixtures.PreviewPng());

            void Write(string name, byte[] bytes)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        var inspection = MapProjectStore.Inspect(path);
        Assert.Equal(ProjectFileStatus.MigrationRequired, inspection.Status);

        var migratedPath = workspace.File("stage2-v2.mapproj");
        var first = MapProjectStore.MigrateCopy(path, migratedPath).Project.Document;
        var second = MapProjectStore.Open(migratedPath).Document;

        Assert.Equal(2, first.Layers.Count);
        Assert.IsType<BaseColorLayer>(first.Layers[0]);
        var objects = Assert.IsType<ObjectLayer>(first.Layers[1]);
        Assert.Equal(ObjectId.Parse(objectId), objects.Objects.Single().Id);
        Assert.Equal(first.BaseColor.Id, second.BaseColor.Id);
    }

    [Fact]
    public void Brush_and_owner_mask_survive_save_reopen_with_exact_tile_bytes()
    {
        using var workspace = new TempWorkspace();
        var brushId = LayerId.New();
        var objectId = LayerId.New();
        var brushPixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        brushPixels[0] = 12;
        brushPixels[3] = 255;
        var maskPixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        maskPixels[17] = 0;
        var brushTiles = TileMap.Empty(4).With(new TileKey(3, 4), brushPixels);
        var maskTiles = TileMap.Empty(1).With(new TileKey(7, 8), maskPixels);
        var document = new MapDocument(
            DocumentId.New(), "tiles", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFEEEEEE"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFEEEEEE"),
                new BrushLayer(brushId, "paint", true, false, 0.75, brushTiles),
                new ObjectLayer(objectId, "masked", true, false, 1, [], new LayerMask(maskTiles, false)),
                new ObjectLayer(LayerId.New(), "neighbour", true, false, 1, []),
            ]);
        var path = workspace.File("tiles.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        var brush = Assert.IsType<BrushLayer>(reopened.FindLayer(brushId));
        Assert.Equal(brushPixels, brush.Tiles.TryGet(new TileKey(3, 4)));
        var masked = Assert.IsType<ObjectLayer>(reopened.FindLayer(objectId));
        Assert.False(masked.Mask!.Enabled);
        Assert.Equal(maskPixels, masked.Mask.Tiles.TryGet(new TileKey(7, 8)));
        Assert.Null(((ObjectLayer)reopened.Layers[^1]).Mask);

        using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry($"rasters/{brushId}/3_4.bin"));
        Assert.NotNull(archive.GetEntry($"masks/{objectId}/7_8.bin"));
    }

    [Fact]
    public void Corrupt_tile_reports_typed_error_with_layer_and_coordinate()
    {
        using var workspace = new TempWorkspace();
        var brushId = LayerId.New();
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        pixels[3] = 255;
        var document = new MapDocument(
            DocumentId.New(), "tiles", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new BrushLayer(
                    brushId, "paint", true, false, 1,
                    TileMap.Empty(4).With(new TileKey(1, 2), pixels)),
            ]);
        var path = workspace.File("corrupt.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var name = $"rasters/{brushId}/1_2.bin";
            archive.GetEntry(name)!.Delete();
            var replacement = archive.CreateEntry(name);
            using var stream = replacement.Open();
            stream.Write(new byte[TileMap.TileSize * TileMap.TileSize * 4]);
        }

        var error = Assert.Throws<MapProjectTileCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Equal(brushId.ToString(), error.LayerId);
        Assert.Equal(1, error.TileX);
        Assert.Equal(2, error.TileY);
    }
}
