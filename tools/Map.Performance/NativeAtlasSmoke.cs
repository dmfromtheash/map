using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R25: --profile native-atlas --output artifacts/r25/atlas.json.
    // Изолированный профиль задаётся NativeRegionalSmoke. Живые PrintWindow-снимки:
    // Map.LiveCheck --map <atlas-workspace/parent.mapproj> --atlas true --out artifacts/r25/live.
    private async Task VerifyAtlas()
    {
        async Task Show(string path)
        {
            var document = _store.Open(path).Document;
            _editor = new EditorViewModel(document, path, false, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
                assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
            _editor.AtlasMapRequested += (_, target) => _ = Show(target);
            _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
            _window.Content = _view; await _editor.RefreshAtlasAsync(); await Settle(); _view.Canvas.FitToCanvas(); await Settle();
        }
        var parent = PosterStandardFixture() with { Title = "Атлас · мир" };
        var parentPath = Path.Combine(_workspace, "parent.mapproj");
        MapProjectStore.Save(parent, parentPath, new("r25", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(parent));
        await Show(parentPath);
        _picker.Path = Path.Combine(_workspace, "region.mapproj");
        _view!.OpenCanvasWorkflow(); await Settle();
        _editor.SetCanvasWorkflowMode(ResizeRegionMode.Region);
        _editor.RegionXText = "300"; _editor.RegionYText = "100";
        _editor.RegionWidthText = "250"; _editor.RegionHeightText = "250"; _editor.RegionTitleText = "Атлас · регион";
        await Settle();
        var dialog = _window.OwnedWindows.Single(w => w.Name == "CanvasWorkflowWindow");
        Click(dialog, "CreateRegionMapButton");
        await WaitUntil(() => File.Exists(_picker.Path) && !_editor.IsCreatingRegionMap, "Сохранение региона в атласе");
        await _editor.RefreshAtlasAsync(); await Settle();
        Check("Регион появился в атласе", _editor.Atlas is { Maps.Length: 2, Links.Length: 1 });
        var child = _editor.AtlasChildren.Single();
        Check("Преобразование региона совпадает с рамкой", child.Link.Transform == new AtlasTransform(300, 100, 250, 250, 250, 250));
        Check("Миниатюра подкарты видна на родителе", child.Preview is { Length: > 0 }
            && _view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AtlasChild_" + child.Map.Id.ToString("N")).IsEffectivelyVisible);
        Click(_view, "AtlasChild_" + child.Map.Id.ToString("N"));
        await WaitUntil(() => _editor.Session.Document.Id.Value == child.Map.Id, "Переход в подкарту"); await Settle();
        Check("Крошки содержат родителя и подкарту", _editor.AtlasBreadcrumbs.Length == 2);
        var up = _view!.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AtlasUpButton");
        Check("Кнопка Наверх доступна", up.IsEnabled && up.IsEffectivelyVisible);
        Click(_view, "AtlasUpButton"); await WaitUntil(() => _editor.Session.Document.Id == parent.Id, "Наверх"); await Settle();
        Check("Наверх открывает родителя", _editor.DocumentPath == parentPath);
        Click(_view!, "AtlasChild_" + child.Map.Id.ToString("N"));
        await WaitUntil(() => _editor.Session.Document.Id.Value == child.Map.Id, "Повторный переход"); await Settle();
        Click(_view!, "AtlasCrumb_" + parent.Id.Value.ToString("N"));
        await WaitUntil(() => _editor.Session.Document.Id == parent.Id, "Крошка родителя"); await Settle();
        Check("Крошка открывает родителя", _editor.DocumentPath == parentPath);
        Check("Проверка здорового атласа", (await _editor.CheckAtlasAsync()).IsEmpty);
        Click(_view!, "AtlasCheckButton"); await Settle();
        var checkWindow = _window.OwnedWindows.Single(w => w.Name == "AtlasCheckWindow");
        Check("Окно проверки доступно", checkWindow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "AtlasCheckSummary" && t.Text!.Contains("Проблем не найдено")));
        Click(checkWindow, "AtlasCheckCloseButton");

        var hex = HexMapFactory.Create("Атлас · гексы", DateTimeOffset.UtcNow, new("hex"), HexOrientation.Flat, 40, 8, 8, parent.Canvas.Background);
        var hexPath = Path.Combine(_workspace, "hex.mapproj");
        MapProjectStore.Save(hex, hexPath, new("r25", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(hex));
        await Show(hexPath);
        var targetHex = _editor.ActiveHexLayer!.Board.Layout.FromOffset(new(2, 2));
        var childPath = await _editor.PrepareHexChildMapAsync(targetHex);
        Check("Гексовая подкарта создана", childPath is not null && File.Exists(childPath));
        await _editor.RefreshAtlasAsync();
        Check("Гексовая подкарта зарегистрирована", _editor.Atlas is { Maps.Length: 2, Links.Length: 1 } && _editor.Atlas.Links[0].Kind == "hex");
        Check("Связь ключа гекса сохранена сразу", _store.Open(hexPath).Document.Layers.OfType<HexLayer>().Single().Notes[targetHex].ChildMap == Path.GetFileName(childPath));
        var hexLink = _editor.Atlas!.Links[0]; var point = hexLink.Transform.ToChild(hexLink.Transform.X, hexLink.Transform.Y);
        Check("Преобразование гекса обратимо", point == (0d, 0d) && hexLink.Transform.ToParent(0, 0) == (hexLink.Transform.X, hexLink.Transform.Y));
        var depth = _editor.Session.UndoDepth; _editor.Undo();
        Check("Ссылка гекса отменяется", _editor.Session.UndoDepth == depth - 1 && _editor.ActiveHexLayer!.Notes.GetValueOrDefault(targetHex)?.ChildMap is null);
        _editor.Redo();
        Check("Ссылка гекса повторяется", _editor.ActiveHexLayer!.Notes[targetHex].ChildMap is not null);
        await Show(parentPath);
    }
}
