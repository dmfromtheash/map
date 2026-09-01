using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Presentation.Tests;

public sealed class Stage16CVectorTextNotesTests
{
    [Fact]
    public void Explicit_node_and_portal_selection_edit_the_selected_identity_only()
    {
        var editor = Editor();
        editor.AddLine();
        var original = editor.Session.SelectedObject!;
        var first = original.Line!.Nodes[0];
        var second = original.Line.Nodes[1];
        Assert.True(editor.SelectVectorNode(second.Id));
        Assert.True(editor.SetSelectedVectorNode(.25, .72, VectorNodeKind.Curve,
            -.1, .04, .12, -.03));
        var edited = editor.Session.SelectedObject!;
        Assert.Equal(first, edited.Line!.Nodes[0]);
        Assert.Equal((.25, .72, VectorNodeKind.Curve),
            (edited.Line.Nodes[1].X, edited.Line.Nodes[1].Y, edited.Line.Nodes[1].Kind));

        editor.AddPortalToSelected(.2, 18);
        var firstPortal = editor.SelectedVectorPortal!;
        editor.AddPortalToSelected(.8, 44);
        Assert.True(editor.SelectVectorPortal(firstPortal.Id));
        Assert.True(editor.SetSelectedPortal(.35, 26));
        var portals = editor.Session.SelectedObject!.Line!.Portals;
        Assert.Equal((.35, 26), (portals[0].Position, portals[0].Width));
        Assert.Equal((.8, 44), (portals[1].Position, portals[1].Width));

        Assert.True(editor.SelectVectorNode(second.Id));
        var node = editor.SelectedVectorNode!;
        var worldX = edited.X + node.X * edited.Width;
        var worldY = edited.Y + node.Y * edited.Height;
        var revision = editor.Session.Revision;
        Assert.True(editor.BeginVectorElementDrag(worldX, worldY, 1));
        editor.UpdateVectorElementDrag(worldX + 30, worldY + 20);
        Assert.Equal(revision, editor.Session.Revision);
        editor.EndVectorElementDrag();
        Assert.Equal(revision + 1, editor.Session.Revision);
    }

    [Fact]
    public void Cancelled_vector_gesture_leaves_document_and_history_unchanged()
    {
        var editor = Editor();
        var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
        editor.ActiveTool = EditorTool.Line;
        Assert.True(editor.BeginVectorDraw(20, 20));
        editor.ContinueVectorDraw(90, 40);
        Assert.Single(editor.PlacementPreviewObjects);

        editor.CancelVectorDraw();

        Assert.Same(before, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Empty(editor.PlacementPreviewObjects);
    }

    [Fact]
    public void Boolean_uses_two_explicitly_selected_shapes_not_layer_position()
    {
        var editor = Editor();
        editor.AddShape(ShapePrimitiveKind.Rectangle); var first = editor.Session.SelectedObjectId!.Value;
        editor.AddShape(ShapePrimitiveKind.Ellipse); var untouched = editor.Session.SelectedObjectId!.Value;
        editor.AddShape(ShapePrimitiveKind.Rectangle); var third = editor.Session.SelectedObjectId!.Value;
        editor.Session.SetSelection([first, third]);

        Assert.True(editor.PreviewShapeBoolean(ShapeBooleanOperation.Subtract));
        Assert.Single(editor.PlacementPreviewObjects);
        Assert.True(editor.CommitShapeBooleanPreview());

        var shapes = editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.Contains(shapes, item => item.Id == untouched);
        Assert.DoesNotContain(shapes, item => item.Id == first || item.Id == third);
        Assert.Equal(2, shapes.Count(item => item.Shape is not null));
    }

    [Fact]
    public void Text_inheritance_is_gated_and_click_drag_commits_one_object()
    {
        var template = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "template",
            30, 30, 280, 90, 13, "#00000000", "#00000000", 0, true, false)
        {
            Sublayer = 3, ObjectOpacity = .72,
            Text = new TextStyle { Content = "template", FontSize = 71, FontWeight = 700,
                FillColor = "#FF336699", RequestedFontFamily = "Missing Stage16 Family" },
        };
        var editor = Editor(template);
        editor.Session.SelectedObjectId = template.Id;
        editor.ActiveTool = EditorTool.Text;
        editor.TextInheritFromSelection = false;
        Assert.True(editor.BeginTextPlacement(120, 100)); editor.EndTextPlacement();
        var plain = editor.Session.SelectedObject!;
        Assert.Equal(32, plain.Text!.FontSize);

        editor.Session.SelectedObjectId = template.Id;
        editor.TextInheritFromSelection = true;
        Assert.True(editor.BeginTextPlacement(200, 160));
        editor.ContinueTextPlacement(520, 300);
        Assert.Single(editor.PlacementPreviewObjects);
        editor.EndTextPlacement();
        var inherited = editor.Session.SelectedObject!;
        Assert.Equal(71, inherited.Text!.FontSize);
        Assert.Equal(700, inherited.Text.FontWeight);
        Assert.Equal("Missing Stage16 Family", inherited.Text.RequestedFontFamily);
        Assert.Equal("Новый текст\nна карте", inherited.Text.Content);
        Assert.Equal((200d, 160d, 320d, 140d), (inherited.X, inherited.Y, inherited.Width, inherited.Height));
        Assert.Equal(3, inherited.Sublayer);
    }

    [Fact]
    public void Markdown_preview_uses_safe_subset_and_notes_never_change_image_export()
    {
        const string markdown = "## Заголовок\n- **Жирный** и *курсив*\n[Локально](guide/page)";
        var rendered = NoteMarkdown.RenderPlain(markdown);
        Assert.Contains("Заголовок", rendered);
        Assert.Contains("• Жирный и курсив", rendered);
        Assert.Contains("Локально (guide/page)", rendered);
        Assert.Throws<ArgumentException>(() => NoteMarkdown.RenderPlain("<script>alert(1)</script>"));
        Assert.Throws<ArgumentException>(() => NoteMarkdown.RenderPlain("[x](file:///secret)"));

        var editor = Editor();
        var without = ThumbnailRenderer.RenderPng(editor.Session.Document, 128, 96);
        editor.AddNoteAtViewCentre();
        var note = editor.Session.Document.Notes.Single();
        editor.EditNote(note.Id, "Formatted", markdown);
        var with = ThumbnailRenderer.RenderPng(editor.Session.Document, 128, 96);
        Assert.Equal(without, with);
    }

    [Fact]
    public async Task Shape_texture_controls_survive_reopen_and_keep_export_pixels_exact()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-stage16c", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "shape-texture-controls.mapproj");
            var editor = Editor();
            editor.AddShape(ShapePrimitiveKind.Rectangle);
            editor.SetShapeFillTextureScale(2.75);
            editor.SetShapeFillTextureRotation(37);
            editor.SetShapeFillTextureOffsetX(11);
            editor.SetShapeFillTextureOffsetY(-8);
            var before = editor.Session.SelectedObject!;

            MapProjectStore.Save(editor.Session.Document, path,
                new SaveMetadata("stage16", DateTimeOffset.UnixEpoch),
                ThumbnailRenderer.RenderPng(editor.Session.Document, 96, 64));
            var reopened = MapProjectStore.Open(path).Document;
            var after = reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single();

            Assert.Equal(2.75, after.Shape!.Fill.TextureScale, 6);
            Assert.Equal(37, after.Shape.Fill.TextureRotation, 6);
            Assert.Equal(11, after.Shape.Fill.TextureOffsetX, 6);
            Assert.Equal(-8, after.Shape.Fill.TextureOffsetY, 6);
            Assert.Equal(before.Shape!.Fill, after.Shape.Fill);

            var exporter = new ImageExportEngine();
            var request = new ExportRequest(
                ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
                512, 384, ExportOutputMode.SingleImage, false, false, 100);
            var originalPreview = await exporter.RenderPreviewAsync(
                editor.Session.Document, exporter.Plan(editor.Session.Document, request), null);
            var reopenedPreview = await exporter.RenderPreviewAsync(
                reopened, exporter.Plan(reopened, request), null);
            Assert.Equal(originalPreview.PngBytes, reopenedPreview.PngBytes);

            editor.ResetShapeFillTexture();
            var reset = editor.Session.SelectedObject!.Shape!.Fill;
            Assert.Equal(1, reset.TextureScale);
            Assert.Equal(0, reset.TextureRotation);
            Assert.Equal(0, reset.TextureOffsetX);
            Assert.Equal(0, reset.TextureOffsetY);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EditorViewModel Editor(params SceneObject[] objects)
    {
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "stage16c", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"), layer]);
        return new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
    }
}
