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
        // R28b: a road across the future region, for «Согласование» below.
        var road = Map.Core.Editing.RoadEditor.NewRoad([new(0, 200), new(640, 200)], 6, RoadStyle.Country(), "Тракт");
        var firstObjects = parent.Layers.OfType<ObjectLayer>().First();
        parent = parent.WithLayers([.. parent.Layers.Select(l => l.Id == firstObjects.Id ? firstObjects with { Roads = [road] } : l)]);
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
        // DEC-070: the world functions live in «Мир», which a user opens first.
        Click(_view!, "WorldSectionButton"); await Settle();
        Click(_view!, "AtlasCheckButton"); await Settle();
        Check("Выбор в «Мире» закрывает список", _view!.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorldSection").IsVisible == false);
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
        await VerifyScaleSync(Path.Combine(_workspace, "region.mapproj"), road.Id);
        await VerifyActionSearch();
    }

    /// <summary>
    /// DEC-070 step 4: Ctrl+K opens «Найти действие»; «время» + Enter opens «Мир» and lights «Время» there without
    /// editing the map; «красота» with nothing selected keeps the search open and says what is needed first.
    /// </summary>
    private async Task VerifyActionSearch()
    {
        T Named<T>(string name) where T : Control => _view!.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        var depth = _editor.Session.UndoDepth;
        _view!.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.K,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control, Source = _view });
        await Settle();
        Check("Ctrl+K открывает поиск действий", Named<Border>("ActionSearch").IsVisible);
        var box = Named<TextBox>("ActionSearchBox");
        box.Text = "время"; await Settle();
        Capture(_view, ".action-search");
        Check("Поиск находит «Время»", Named<Button>("ActionResult0").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Время"));
        box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = box });
        await Settle(); await Task.Delay(100); await Settle();
        Capture(_view, ".action-search-found");
        Check("Enter открывает «Мир» и подсвечивает «Время»", Named<Border>("WorldSection").IsVisible
            && Named<Button>("TimelineButton").Classes.Contains("search-found") && !Named<Border>("ActionSearch").IsVisible);
        _view.OpenActionSearch(); box.Text = "красота"; await Settle();
        box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = box });
        await Settle();
        Check("«Красота» без выделения объясняет, что выделить", Named<Border>("ActionSearch").IsVisible
            && Named<TextBlock>("ActionSearchHint") is { IsVisible: true, Text: { } hint } && hint.Contains("выделите", StringComparison.Ordinal));
        box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, Source = box });
        await Settle();
        Check("Поиск не меняет карту", _editor.Session.UndoDepth == depth && !Named<Border>("ActionSearch").IsVisible);
    }

    /// <summary>
    /// R28b through «Согласование»: «Проверить подкарты» plans the road into the region; the region is then
    /// saved elsewhere - first with the copy already there and edited by hand (the old plan is refused and the
    /// file kept), then with a path of its own (the road is written and the path stays).
    /// </summary>
    private async Task VerifyScaleSync(string regionPath, Guid roadId)
    {
        Click(_view!, "WorldSectionButton"); await Settle();
        Click(_view!, "ScaleSyncButton"); await Settle();
        var window = _window.OwnedWindows.Single(w => w.Name == "ScaleSyncWindow");
        var regionRoads = () => _store.Open(regionPath).Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).ToList();
        async Task Press(string name, Func<bool> done, string what)
        {
            Click(window, name);
            await WaitUntil(done, what); await Task.Delay(300); await Settle();
        }
        string Summary() => string.Join(" | ", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        await Press("ScaleSyncCheckButton", () => Summary().Contains("новых: 1", StringComparison.Ordinal), "План согласования");
        Check("Согласование: план видит дорогу родителя", Summary().Contains("новых: 1", StringComparison.Ordinal));

        // Elsewhere the copy was already written and then bent by hand; the region saved after the plan.
        var region = _store.Open(regionPath).Document;
        var transform = _editor.Atlas!.Links.Single(l => l.Kind == "region").Transform;
        var synced = Map.Core.Editing.ScaleSync.Apply(region, Map.Core.Editing.ScaleSync.Plan(_editor.Session.Document, region, transform));
        var copy = synced.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Single(r => r.Inherited?.SourceId == roadId);
        var bent = synced.WithLayers([.. synced.Layers.Select(l => l is ObjectLayer o
            ? o with { Roads = [.. o.Roads.Select(r => r.Id == copy.Id ? r with { Nodes = [r.Nodes[0] with { Y = r.Nodes[0].Y + 40 }, .. r.Nodes.Skip(1)] } : r)] } : l)]);
        MapProjectStore.Save(bent, regionPath, new("r28b", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(bent));
        await Press("ScaleSyncApplyButton", () => _editor.StatusDetail.Contains("изменилось после проверки", StringComparison.Ordinal), "Отказ устаревшего плана");
        Check("Согласование: устаревший план не записан", _editor.StatusDetail.Contains("изменилось после проверки", StringComparison.Ordinal)
            && Math.Abs(regionRoads().Single(r => r.Id == copy.Id).Nodes[0].Y - (copy.Nodes[0].Y + 40)) < .01);

        // Back to the region without the copy; checked; then a path drawn there and saved - the road goes in, the path stays.
        MapProjectStore.Save(region, regionPath, new("r28b", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(region));
        await Press("ScaleSyncCheckButton", () => Summary().Contains("новых: 1", StringComparison.Ordinal), "План по исходной подкарте");
        var path = Map.Core.Editing.RoadEditor.NewRoad([new(10, 10), new(60, 60)], 3, RoadStyle.Country(), "Тропа");
        var withPath = region.Layers.OfType<ObjectLayer>().FirstOrDefault() is { } host
            ? region.WithLayers([.. region.Layers.Select(l => l.Id == host.Id ? host with { Roads = [.. host.Roads, path] } : l)])
            : region.WithLayers([.. region.Layers, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = [path] }]);
        MapProjectStore.Save(withPath, regionPath, new("r28b", DateTimeOffset.UtcNow), Map.Rendering.ThumbnailRenderer.RenderPng(withPath));
        await Press("ScaleSyncApplyButton", () => _editor.StatusDetail.StartsWith("Согласовано подкарт: 1", StringComparison.Ordinal), "Запись в подкарту");
        var written = regionRoads();
        Check("Согласование: запись не теряет сохранённое после плана", written.Any(r => r.Id == path.Id) && written.Any(r => r.Inherited?.SourceId == roadId));
        window.Close(); await Settle();
    }
}
