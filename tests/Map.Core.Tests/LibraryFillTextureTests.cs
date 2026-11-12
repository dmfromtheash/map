using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>A library texture is an <c>asset:</c> id plus an embedded copy that the edit brings into the map.</summary>
public sealed class LibraryFillTextureTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Library_ids_are_the_prefix_and_a_lower_case_sha256()
    {
        var id = MapFillTextures.AssetId(Sha.ToUpperInvariant());
        Assert.Equal("asset:" + Sha, id);
        Assert.True(MapFillTextures.IsValidId(id));
        Assert.True(MapFillTextures.IsAssetId(id));
        Assert.Equal(Sha, MapFillTextures.AssetSha256(id));
        Assert.Equal(MapFillTextureKind.Ground, MapFillTextures.Find(id)!.Kind);
        Assert.True(MapFillTextures.Accepts(id, MapFillTextureKind.Water));
        Assert.True(MapFillTextures.Accepts(id, MapFillTextureKind.Ground));

        foreach (var bad in new[] { "asset:" + Sha.ToUpperInvariant(), "asset:" + Sha[..63], "asset:" + Sha + "0", "asset:" + new string('g', 64), "asset:", "Asset:" + Sha })
        {
            Assert.False(MapFillTextures.IsValidId(bad), bad);
            Assert.Null(MapFillTextures.AssetSha256(bad));
        }
        Assert.Throws<ArgumentException>(() => MapFillTextures.AssetId("C:\\Library\\stone.png"));
        Assert.Null(MapFillTextures.AssetSha256(MapFillTextures.OwnerSnow));
        Assert.False(MapFillTextures.Accepts(MapFillTextures.OwnerSnow, MapFillTextureKind.Water));
        Assert.True(MapFillTextures.Accepts(MapFillTextures.OwnerSnow, MapFillTextureKind.Ground));
    }

    [Fact]
    public void A_road_takes_a_library_texture_and_refuses_a_malformed_one()
    {
        var id = MapFillTextures.AssetId(Sha);
        Assert.Null(Record.Exception(() => new RoadStyle { TextureId = id }.Validate()));
        Assert.Throws<ArgumentException>(() => new RoadStyle { TextureId = "asset:nope" }.Validate());
    }

    [Fact]
    public void An_edit_that_starts_using_a_library_texture_embeds_it_in_the_same_undo_step()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var document = new MapDocument(DocumentId.New(), "Текстуры", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land]);
        var asked = new List<string>();
        var session = new EditingSession(document)
        {
            FillTextureAssetSource = sha => { asked.Add(sha); return Asset(sha); },
        };
        var id = MapFillTextures.AssetId(Sha);

        session.Execute(new UpdateLandStyleCommand(land, land.EdgeStyle with { FillTextureId = id }));
        Assert.Equal(id, session.Document.Layers.OfType<LandLayer>().Single().EdgeStyle.FillTextureId);
        Assert.Equal(Sha, Assert.Single(session.Document.Assets).Sha256);
        Assert.Equal(1, session.UndoDepth);

        // A later edit that keeps the texture does not ask the library again.
        session.Execute(new UpdateLandStyleCommand(session.Document.Layers.OfType<LandLayer>().Single(),
            land.EdgeStyle with { FillTextureId = id, FillTextureOpacity = .4 }));
        Assert.Single(asked);

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(land.EdgeStyle.FillTextureId, session.Document.Layers.OfType<LandLayer>().Single().EdgeStyle.FillTextureId);
        Assert.Empty(session.Document.Assets);
        Assert.True(session.Redo());
        Assert.Equal(Sha, Assert.Single(session.Document.Assets).Sha256);
        Assert.Equal(id, session.Document.Layers.OfType<LandLayer>().Single().EdgeStyle.FillTextureId);
    }

    [Fact]
    public void A_texture_the_library_cannot_supply_stays_an_id_without_bytes()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var document = new MapDocument(DocumentId.New(), "Текстуры", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land]);
        var session = new EditingSession(document) { FillTextureAssetSource = _ => null };
        session.Execute(new UpdateLandStyleCommand(land, land.EdgeStyle with { FillTextureId = MapFillTextures.AssetId(Sha) }));
        Assert.Empty(session.Document.Assets);
        Assert.Equal([Sha], FillTextureAssets.MissingHashes(session.Document));
        Assert.True(FillTextureAssets.UsesLibraryTextures(session.Document.Layers));
    }

    private static MapAssetReference Asset(string sha) =>
        new(sha, "Камень", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "test", "test", [1, 2, 3]);
}
