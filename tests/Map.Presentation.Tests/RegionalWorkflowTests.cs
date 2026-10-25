using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class RegionalWorkflowTests
{
    private static SceneObject Stamp(string sha, double x, bool locked=false) => new(ObjectId.New(), SceneObjectKind.Stamp,
        "Stamp",x,80,100,140,17,"#FFFFFFFF","#FF000000",0,true,locked)
        { Stamp=new(sha){ReferenceWidth=100,ReferenceHeight=140,FlipHorizontal=true} };
    private static MapDocument Document(params SceneObject[] items) => TestData.Document().WithLayers([
        new BaseColorLayer(LayerId.New(),"Фон",true,false,1,"#FFF0E0C0"),
        new ObjectLayer(LayerId.New(),"Объекты",true,false,1,items)]);
    private static EditorViewModel Editor(MapDocument document,AssetCatalog? catalog=null) => new(document,null,false,
        new FakeProjectStore(),new FakeRecentMaps(),new FakeFilePicker(),new FakePrompts(),new FakeLog(),assetCatalog:catalog);

    [Fact]
    public void Stamp_transfer_preview_cancel_commit_and_undo_preserve_identity_geometry_mask_and_locked_targets()
    {
        var source=Stamp(new string('a',64),100); source=source with { Stamp=source.Stamp! with { Hue=28,ShadowOpacity=.45 },ObjectOpacity=.7 };
        var target=Stamp(new string('b',64),300); target=target with { Stamp=target.Stamp! with { MaskAssetSha256=new string('c',64),ClipMaskEnabled=true } };
        var locked=Stamp(new string('d',64),500,true);
        var document=Document(source,target,locked); var editor=Editor(document);
        editor.SelectObject(source.Id); Assert.True(editor.CopyAppearance());
        editor.Session.SetSelection([target.Id,locked.Id]);
        Assert.True(editor.PreviewAppearanceTransfer()); Assert.Same(document,editor.Session.Document); Assert.Equal(0,editor.Session.UndoDepth);
        var shown=DocumentEditor.FindObject(editor.RenderDocument,target.Id)!;
        Assert.Equal(28,shown.Stamp!.Hue); Assert.Equal(target.X,shown.X); Assert.Equal(target.Stamp.AssetSha256,shown.Stamp.AssetSha256);
        Assert.Equal(target.Stamp.MaskAssetSha256,shown.Stamp.MaskAssetSha256); Assert.True(shown.Stamp.ClipMaskEnabled);
        Assert.True(shown.Stamp.FlipHorizontal); Assert.Equal(target.Width,shown.Width); Assert.Equal(target.RotationDegrees,shown.RotationDegrees);
        Assert.Equal(locked,DocumentEditor.FindObject(editor.RenderDocument,locked.Id));
        Assert.True(editor.HandleEscape()); Assert.Same(document,editor.RenderDocument);
        Assert.True(editor.PreviewAppearanceTransfer()); Assert.True(editor.ApplyAppearanceTransfer()); Assert.Equal(1,editor.Session.UndoDepth);
        editor.Undo(); Assert.Equal(target,DocumentEditor.FindObject(editor.Session.Document,target.Id));
        editor.Redo(); Assert.Equal(shown,DocumentEditor.FindObject(editor.Session.Document,target.Id));
    }

    [Fact]
    public void Changing_selection_invalidates_a_preview_without_applying_to_old_targets()
    {
        var a=Stamp(new string('a',64),100); a=a with { Stamp=a.Stamp! with { Brightness=.3 } };
        var b=Stamp(new string('b',64),300);var c=Stamp(new string('c',64),500);var editor=Editor(Document(a,b,c));
        editor.SelectObject(a.Id);editor.CopyAppearance();editor.SelectObject(b.Id);Assert.True(editor.PreviewAppearanceTransfer());
        editor.SelectObject(c.Id);Assert.False(editor.HasAppearanceTransferPreview);Assert.False(editor.ApplyAppearanceTransfer());
        Assert.Equal(0,editor.Session.UndoDepth);Assert.Equal(b,DocumentEditor.FindObject(editor.Session.Document,b.Id));
    }

    [Fact]
    public void Text_transfer_preserves_words_curve_flips_and_bounds()
    {
        var source=Stamp(new string('a',64),10) with {Kind=SceneObjectKind.Text,Stamp=null,Text=new(){Content="Источник",FontSize=47,FillColor="#FF123456"}};
        var target=Stamp(new string('b',64),330) with {Kind=SceneObjectKind.Text,Stamp=null,Text=new(){Content="Другое имя",Curve=.25,FlipVertical=true}};
        var editor=Editor(Document(source,target));editor.SelectObject(source.Id);editor.CopyAppearance();editor.SelectObject(target.Id);
        Assert.True(editor.PreviewAppearanceTransfer());Assert.True(editor.ApplyAppearanceTransfer());
        var result=DocumentEditor.FindObject(editor.Session.Document,target.Id)!;
        Assert.Equal("Другое имя",result.Text!.Content);Assert.Equal(.25,result.Text.Curve);Assert.True(result.Text.FlipVertical);
        Assert.Equal(47,result.Text.FontSize);Assert.Equal(target.X,result.X);Assert.Equal(target.Width,result.Width);
    }

    [Fact]
    public void Road_branch_transfer_preserves_shared_nodes_widths_and_other_branches()
    {
        var source=RoadEditor.NewRoad([new(0,0),new(100,100)],14,RoadStyle.Country() with { SurfaceColor="#FF123456" });
        var target=RoadEditor.NewRoad([new(200,100),new(500,100)],27);
        var branch=target.Branches[0] with {Id=Guid.NewGuid(),Name="Вторая ветвь"}; target=target with {Branches=[target.Branches[0],branch]};
        var layer=new ObjectLayer(LayerId.New(),"Дороги",true,false,1,[]){Roads=[source,target]};
        var editor=Editor(TestData.Document().WithLayers([new BaseColorLayer(LayerId.New(),"Фон",true,false,1,"#FFFFFFFF"),layer])); editor.SelectRoad(source.Id);editor.RoadLevel=RoadSelectionLevel.Road;
        Assert.True(editor.CopyAppearance());editor.SelectRoad(target.Id,branch.Id);editor.RoadLevel=RoadSelectionLevel.Branch;
        Assert.True(editor.PreviewAppearanceTransfer());Assert.True(editor.ApplyAppearanceTransfer());
        var result=editor.Roads.Single(r=>r.Id==target.Id);
        Assert.Equal(target.Nodes,result.Nodes);Assert.Equal(target.Style,result.Style);Assert.Equal(target.Branches[0],result.Branches[0]);
        Assert.Equal(branch.Vertices,result.Branches[1].Vertices);Assert.Equal(source.Style,result.Branches[1].Style);
        Assert.Equal(1,editor.Session.UndoDepth);editor.Undo();Assert.Equal(target,editor.Roads.Single(r=>r.Id==target.Id));
    }

    [Fact]
    public async Task Placement_and_appearance_presets_share_the_existing_store_and_reload_without_document_edits()
    {
        var root=Path.Combine(Path.GetTempPath(),"map-regional-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog=new AssetCatalog(root);
            var stamp=Stamp(new string('a',64),50) with {ObjectOpacity=.63};var editor=Editor(Document(stamp),catalog);
            Assert.True(editor.SaveBrushPreset("Старая кисть"));editor.SelectObject(stamp.Id);Assert.True(editor.CopyAppearance());
            Assert.True(editor.SaveAppearancePreset("Тёплый рисунок"));
            var reloaded=Editor(Document(stamp),catalog);
            Assert.Single(reloaded.BrushPresets);var preset=Assert.Single(reloaded.RegionalPresets);
            Assert.True(reloaded.ApplyRegionalPreset(preset.Id));Assert.Equal(0,reloaded.Session.UndoDepth);
            Assert.True(reloaded.RenameBrushPreset(preset.Id,"Тёплая графика"));
            Assert.Equal("Тёплая графика",Assert.Single(new BrushPresetStore(root).Load(),p=>p.Appearance is not null).Name);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_and_template_thumbnails_use_embedded_images(bool template)
    {
        using var surface=SKSurface.Create(new SKImageInfo(24,24))!;surface.Canvas.Clear(SKColors.LimeGreen);
        using var image=surface.Snapshot();using var encoded=image.Encode(SKEncodedImageFormat.Png,100);var bytes=encoded.ToArray();
        var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var stamp=Stamp(sha,100);var doc=Document(stamp).WithAssets([new(sha,"Green","image/png",".png",ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch,"test","synthetic verification",embeddedContent:bytes)]);
        var store=new FakeProjectStore();var source=Path.Combine(Path.GetTempPath(),"source.mapproj");var destination=Path.Combine(Path.GetTempPath(),"copy.mapproj");
        store.Save(doc,source,new("Test",DateTimeOffset.UtcNow),[]);
        var dashboard=new DashboardViewModel(store,new FakeRecentMaps(),new FakeLog(),new FakeFilePicker {NextSavePath=destination});
        Assert.Equal(destination,await dashboard.CloneAsync(new(source,doc.Title,DateTimeOffset.UtcNow,true,null),template));
        var saved=store.Saved[destination];using var images=new ExportStampImageSource(saved.Document,destination,null);
        Assert.Equal(ThumbnailRenderer.RenderPng(saved.Document,stamps:images),saved.Preview);
        Assert.NotEqual(doc.Id,saved.Document.Id);Assert.Same(doc,store.Saved[source].Document);
    }

    [Fact]
    public async Task A_linked_image_is_embedded_in_an_independent_copy_without_changing_the_source_reference()
    {
        var root=Path.Combine(Path.GetTempPath(),"map-linked-copy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var surface=SKSurface.Create(new SKImageInfo(20,20))!;surface.Canvas.Clear(SKColors.Coral);
            using var image=surface.Snapshot();using var encoded=image.Encode(SKEncodedImageFormat.Png,100);var bytes=encoded.ToArray();
            var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            File.WriteAllBytes(Path.Combine(root,"art.png"),bytes);
            var doc=Document(Stamp(sha,100)).WithAssets([new(sha,"Linked","image/png",".png",ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch,"test","synthetic verification",relativePath:"art.png")]);
            var source=Path.Combine(root,"source.mapproj");var destination=Path.Combine(root,"elsewhere","copy.mapproj");
            var store=new FakeProjectStore();store.Save(doc,source,new("Test",DateTimeOffset.UtcNow),[]);
            var dashboard=new DashboardViewModel(store,new FakeRecentMaps(),new FakeLog(),new FakeFilePicker {NextSavePath=destination});
            Assert.Equal(destination,await dashboard.CloneAsync(new(source,doc.Title,DateTimeOffset.UtcNow,true,null)));
            var saved=store.Saved[destination];var asset=Assert.Single(saved.Document.Assets);
            Assert.Equal(ProjectAssetStorage.Embedded,asset.Storage);Assert.Equal(bytes,asset.EmbeddedContent);
            Assert.Equal(ProjectAssetStorage.Linked,doc.Assets[0].Storage);
            using var images=new ExportStampImageSource(saved.Document,destination,null);
            Assert.Equal(ThumbnailRenderer.RenderPng(saved.Document,stamps:images),saved.Preview);
        }
        finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Explicit_pack_repeat_import_preserves_owner_metadata_without_bundled_artwork()
    {
        var root=Path.Combine(Path.GetTempPath(),"map-regional-pack-"+Guid.NewGuid().ToString("N"));
        try
        {
            var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
            var items=new List<RegionalArtItem>();
            foreach(var (id,color) in new[]{("one",SKColors.Coral),("two",SKColors.Teal)})
            {
                using var surface=SKSurface.Create(new SKImageInfo(16,16))!;surface.Canvas.Clear(color);
                using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,100);
                var bytes=data.ToArray();File.WriteAllBytes(Path.Combine(source,id+".png"),bytes);
                var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                items.Add(new(id,"trees",id,id+".png",sha,100));
            }
            var manifest=new RegionalArtManifest("diagnostic","Диагностика",[new("trees","Деревья",[])],items.ToArray());
            File.WriteAllText(Path.Combine(source,"manifest.json"),System.Text.Json.JsonSerializer.Serialize(manifest));
            await using var catalog=new AssetCatalog(Path.Combine(root,"catalog"));
            var pack=RegionalArtPack.Import(catalog,source);var set=pack.Sets["trees"];
            catalog.RenameSet(set.Id,"Мой лес");catalog.SetSetFavorite(set.Id,true);catalog.SetTags(set.Id,["личное"]);
            catalog.ReorderSetVariants(set.Id,set.VariantIds.Reverse().ToArray());
            var variant=set.VariantIds[0];catalog.SetVariantPresentation(variant,"Моя пихта",.4,.82,.27);
            var held=catalog.FindSet(set.Id)!;var heldVariant=catalog.FindVariant(variant)!;
            var again=RegionalArtPack.Import(catalog,source);
            Assert.Equal(pack.Manifest.Assets.Length,catalog.Records.Count);
            Assert.Equal(held,catalog.FindSet(set.Id));Assert.Equal(heldVariant,catalog.FindVariant(variant));
            Assert.Equal(set.Id,again.Sets["trees"].Id);
            var editor=Editor(Document(),catalog);
            Assert.False(editor.CanImportRegionalPack);
            Assert.True(editor.StartRegionalPlacement(RegionalPlacementMode.Forest));
            Assert.Equal(0,editor.Session.UndoDepth);
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Region_file_and_recent_have_the_same_image_preview_and_preserve_the_source(bool linked)
    {
        var root=Path.Combine(Path.GetTempPath(),"map-regional-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var surface=SKSurface.Create(new SKImageInfo(24,24))!;surface.Canvas.Clear(SKColors.Coral);
            using var image=surface.Snapshot();using var encoded=image.Encode(SKEncodedImageFormat.Png,100);var bytes=encoded.ToArray();
            var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));File.WriteAllBytes(Path.Combine(root,"art.png"),bytes);
            var source=Document(Stamp(sha,100)).WithAssets([new(sha,"Coral","image/png",".png",
                linked?ProjectAssetStorage.Linked:ProjectAssetStorage.Embedded,DateTimeOffset.UnixEpoch,"test","synthetic verification",
                relativePath:linked?"art.png":null,embeddedContent:linked?null:bytes)]);
            var store=new FakeProjectStore();var recent=new FakeRecentMaps();var destination=Path.Combine(root,"elsewhere","region.mapproj");
            var editor=new EditorViewModel(source,Path.Combine(root,"source.mapproj"),false,store,recent,
                new FakeFilePicker {NextSavePath=destination},new FakePrompts(),new FakeLog());
            editor.BeginCanvasWorkflow();editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
            editor.RegionXText="0";editor.RegionYText="0";editor.RegionWidthText="500";editor.RegionHeightText="400";
            editor.RegionTitleText="Регион";Assert.True(await editor.CreateMapFromRegionAsync());
            var saved=store.Saved[destination];Assert.Same(source,editor.Session.Document);Assert.Equal(0,editor.Session.UndoDepth);
            Assert.Equal(ProjectAssetStorage.Embedded,Assert.Single(saved.Document.Assets).Storage);
            using var images=new ExportStampImageSource(saved.Document,destination,null);
            Assert.Equal(ThumbnailRenderer.RenderPng(saved.Document,stamps:images),saved.Preview);
            Assert.Equal(saved.Preview,Assert.Single(recent.Items).ThumbnailPng);
        }
        finally {Directory.Delete(root,true);}
    }
}
