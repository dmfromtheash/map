using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// Exploratory pass over every placement function on a copy of the delivered regional map:
/// regional forest/ridge/settlement, editable areas and paths with all their panel buttons,
/// mode and tool switches in the middle of a gesture, Enter/Esc/Undo/Redo. Each step records
/// its handler time, the time to the next rendered frame and the editor state after it.
/// </summary>
public sealed partial class RegionalApplication
{
    private readonly List<string> _explore = [];
    private int _shot;

    private async Task ExploreAreas()
    {
        var source = Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT") ?? throw new InvalidOperationException("MAP_REGIONAL_PROJECT required");
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        Dispatcher.UIThread.UnhandledException += (_, e) => { Log("!! UNHANDLED " + e.Exception); e.Handled = true; };
        RegionalArtPack.Import(_catalog);
        var original = MapProjectStore.Open(source).Document;
        _picker.Path = Path.Combine(_workspace, "explore.mapproj");
        _editor = new EditorViewModel(original, null, false, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        await Settle(); _view.Canvas.FitToCanvas(); await Frame();
        // Work at 100 % around the empty meadow, where a user would draw.
        _editor.Session.Viewport.Set(2200, 600, 1); _view.Canvas.InvalidateVisual(); await Frame();
        await Shot("start");

        await Step("rail: Ассеты", () => ClickName("AssetsLibraryButton"));
        _view.AssetCatalogWindowForSmoke?.Close(); await Settle();
        await Shot("stamp-panel");
        if (Environment.GetEnvironmentVariable("MAP_EXPLORE_PASS") == "2") { await ExploreLarge(); goto done; }

        // ---------------- Forest: full gesture, Enter, Undo, Redo
        await Step("Лес по области", () => ClickName("RegionalForestButton"));
        await Shot("forest-armed");
        var forest = new (double, double)[] { (2400, 800), (2650, 775), (2820, 800), (2830, 950), (2650, 985), (2430, 990) };
        foreach (var (x, y) in forest) await Step($"canvas click {x},{y}", () => ClickWorld(x, y));
        await Shot("forest-draft");
        await Step("Enter (FinishPath key route)", () => Key(Avalonia.Input.Key.Enter));
        await Shot("forest-placed");
        await Step("Undo button", () => ClickName("UndoButton"));
        await Shot("forest-undone");
        await Step("Redo button", () => ClickName("RedoButton"));

        // ---------------- Clicks on the canvas after placing: what does the user get?
        await Step("click inside placed forest", () => ClickWorld(2600, 900));
        await Step("click empty meadow after forest", () => ClickWorld(2300, 1150));
        await Step("click another empty point", () => ClickWorld(2500, 1200));
        await Shot("after-forest-clicks");
        await Step("Esc", () => Key(Avalonia.Input.Key.Escape));
        await Step("Esc again", () => Key(Avalonia.Input.Key.Escape));

        // ---------------- Panel settings of a placed area, each followed by Undo
        await Step("select forest in picker", () => { if (_editor.EditableAreas.LastOrDefault() is { } a) _editor.SelectEditableArea(a.Id); });
        await Step("density 30 (box+Enter)", () => TypeBox("EditableAreaDensityBox", "30"));
        await Step("Undo button after density", () => ClickName("UndoButton"));
        await Step("density 95 (box+Enter)", () => TypeBox("EditableAreaDensityBox", "95"));
        await Step("Ctrl+Z while density box focused", () => CtrlZ("EditableAreaDensityBox"));
        await Step("size variation 0", () => TypeBox("EditableAreaSizeVariationBox", "0"));
        await Step("pattern -> Группы", () => ComboPick("EditableAreaPatternBox", 2));
        await Step("join edges toggle", () =>
        {
            var toggle = _view!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Name == "EditableAreaJoinEdgesToggle");
            toggle.IsChecked = toggle.IsChecked != true; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        await Step("Другой рисунок", () => ClickName("AnotherEditableAreaArrangementButton"));
        await Step("seed 12345", () => TypeBox("EditableAreaSeedBox", "12345"));
        await Shot("forest-edited");
        var depth = _editor.Session.UndoDepth;
        await Step("undo x6", () => { for (var i = 0; i < 6; i++) ClickName("UndoButton"); });
        Log($"   undo depth {depth} -> {_editor.Session.UndoDepth}");

        // ---------------- Vertex drag / insert / delete
        var area = _editor.SelectedEditableArea;
        if (area is not null)
        {
            var n = area.Nodes[2];
            await Step("drag vertex 2 by +120,+60", () => DragWorld(n.X, n.Y, n.X + 120, n.Y + 60));
            await Shot("vertex-dragged");
            await Step("Добавить вершину на ребре", () => ClickName("InsertEditableAreaNodeButton"));
            var a0 = _editor.SelectedEditableArea!.Nodes[0]; var a1 = _editor.SelectedEditableArea!.Nodes[1];
            await Step("click on edge 0-1", () => ClickWorld((a0.X + a1.X) / 2, (a0.Y + a1.Y) / 2));
            await Step("Удалить вершину", () => ClickName("DeleteEditableAreaNodeButton"));
            await Step("Выбрать объект области", () => ClickName("SelectAreaObjectButton"));
            await Step("click a member", () =>
        {
            var areaId = _editor.SelectedEditableArea!.Id;
            var m = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                .First(o => PlacementAreaOwnership.Owner(_editor.Session.Document, o.Id)?.Id == areaId);
            ClickWorld(m.X + m.Width / 2, m.Y + m.Height / 2);
        });
            await Step("Отделить выбранный объект", () => ClickName("DetachAreaObjectButton"));
            await Step("Применить параметры ассета", () => ClickName("ApplyEditableAreaSettingsButton"));
            await Step("Undo x5", () => { for (var i = 0; i < 5; i++) ClickName("UndoButton"); });
        }

        // ---------------- Other placement modes while an area is selected
        await Step("Esc (drop the picked object)", () => Key(Avalonia.Input.Key.Escape));
        await Step("mode: Один", () => ClickName("StampModeOne"));
        await Step("click on canvas in One mode (near forest vertex)", () => ClickWorld(2400, 800));
        await Step("click on canvas in One mode (inside forest)", () => ClickWorld(2600, 900));
        await Shot("one-mode");
        await Step("mode: Серия", () => ClickName("StampModeSeries"));
        await Step("series drag", () => DragWorld(2300, 1300, 2600, 1320));
        await Step("mode: Россыпь", () => ClickName("StampModeArea"));
        await Step("scatter drag", () => DragWorld(2300, 1400, 2500, 1500));
        await Step("mode: Область", () => ClickName("StampModeFillArea"));
        await Step("click inside forest in FillArea mode", () => ClickWorld(2600, 900));
        await Step("click empty in FillArea mode", () => ClickWorld(2250, 1600));
        await Shot("fillarea-mode-clicks");

        // ---------------- Undo in the middle of drawing
        await Step("Лес по области (2)", () => ClickName("RegionalForestButton"));
        await Step("draft click 1", () => ClickWorld(2580, 2240));
        await Step("draft click 2", () => ClickWorld(2740, 2215));
        await Step("draft click 3", () => ClickWorld(2840, 2250));
        var beforeUndo = _editor.Session.UndoDepth;
        await Step("Undo while drafting", () => ClickName("UndoButton"));
        Log($"   undo while drafting: depth {beforeUndo} -> {_editor.Session.UndoDepth}, creating={_editor.IsCreatingEditableArea}");
        await Step("Redo", () => ClickName("RedoButton"));

        // ---------------- Tool switch in the middle of a draft
        await Step("Лес по области (3)", () => ClickName("RegionalForestButton"));
        await Step("draft click a", () => ClickWorld(2580, 2240));
        await Step("draft click b", () => ClickWorld(2740, 2215));
        await Step("rail: Выделение mid-draft", () => ClickName("SelectToolButton"));
        await Step("select-tool click on forest tree", () => ClickWorld(2600, 880));
        await Step("Delete key on area member", () => Key(Avalonia.Input.Key.Delete));
        await Step("Undo after delete", () => ClickName("UndoButton"));
        await Step("rail: Суша", () => ClickName("LandToolButton"));
        await Step("land click", () => ClickWorld(2300, 1700));
        await Step("rail: Реки", () => ClickName("RiverToolButton"));
        await Step("river click", () => ClickWorld(2300, 1700));
        await Step("Esc", () => Key(Avalonia.Input.Key.Escape));
        await Step("rail: Дороги", () => ClickName("RoadToolButton"));
        await Step("road click", () => ClickWorld(2300, 1700));
        await Step("Esc", () => Key(Avalonia.Input.Key.Escape));
        await Shot("other-tools");

        // ---------------- Ridge (editable path)
        await Step("rail: Ассеты", () => ClickName("AssetsLibraryButton"));
        _view.AssetCatalogWindowForSmoke?.Close(); await Settle();
        await Step("Хребет по линии", () => ClickName("RegionalRidgeButton"));
        foreach (var (x, y) in new (double, double)[] { (2250, 1850), (2500, 1800), (2750, 1900), (2950, 1850) })
            await Step($"ridge click {x},{y}", () => ClickWorld(x, y));
        await Shot("ridge-draft");
        await Step("Enter", () => Key(Avalonia.Input.Key.Enter));
        await Shot("ridge-placed");
        await Step("ridge spacing 80", () => TypeBox("EditablePathSpacingBox", "80"));
        await Step("Undo", () => ClickName("UndoButton"));
        await Step("Undo", () => ClickName("UndoButton"));
        await Step("Redo", () => ClickName("RedoButton"));

        // ---------------- Settlement
        await Step("Поселение по области", () => ClickName("RegionalSettlementButton"));
        foreach (var (x, y) in new (double, double)[] { (2250, 2050), (2450, 2030), (2470, 2200), (2260, 2210) })
            await Step($"settlement click {x},{y}", () => ClickWorld(x, y));
        await Step("Разместить button", () => ClickName("FinishEditableAreaButton"));
        await Shot("settlement-placed");
        await Step("Undo", () => ClickName("UndoButton"));
        await Step("Redo", () => ClickName("RedoButton"));

        // ---------------- Bake and delete
        await Step("Превратить область в обычные объекты", () => ClickName("BakeEditableAreaButton"));
        await Step("Undo bake", () => ClickName("UndoButton"));
        await Step("select settlement", () => { if (_editor.EditableAreas.LastOrDefault() is { } a) _editor.SelectEditableArea(a.Id); });
        await Step("Удалить область", () => ClickName("DeleteEditableAreaButton"));
        await Step("Undo delete", () => ClickName("UndoButton"));
        await Shot("end");

        done:
        Log($"final undo depth {_editor.Session.UndoDepth}; source unchanged {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) == sourceHash}");
        File.WriteAllLines(Path.ChangeExtension(Options.OutputPath, ".log.txt"), _explore);
        _report.Checks["explore finished"] = true;
    }

    private async Task Step(string name, Action action)
    {
        var depth = _editor.Session.UndoDepth; var doc = _editor.Session.Document;
        var watch = Stopwatch.StartNew(); string? error = null;
        try { action(); } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        var handler = watch.Elapsed.TotalMilliseconds;
        await Task.WhenAny(Task.WhenAll(_editor.PendingEditableAreaWork, _editor.PendingEditableAreaPreview, _editor.PendingGeneration), Task.Delay(60000));
        if (_editor.GenerationHasPreview) { ClickName("GenerationApplyButton"); await Settle(); }
        var background = watch.Elapsed.TotalMilliseconds;
        await Frame();
        var frame = watch.Elapsed.TotalMilliseconds;
        var e = _editor;
        Log($"{name,-48} handler {handler,7:0} ms  bg {background,7:0}  frame {frame,7:0} ms | tool={e.ActiveTool} mode={e.StampPlacementMode} " +
            $"creatingArea={e.IsCreatingEditableArea} creatingPath={e.IsCreatingEditablePath} area={e.SelectedEditableArea?.Name ?? "-"} " +
            $"path={e.SelectedEditablePath?.Name ?? "-"} pending={e.PendingPlacement?.Mode.ToString() ?? "-"} sel={e.SelectionCount} " +
            $"undo {depth}->{e.Session.UndoDepth} docChanged={!ReferenceEquals(doc, e.Session.Document)} " +
            $"objects={e.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count)}" +
            (error is null ? "" : "  ERROR " + error) + $"\n      status: {e.StatusDetail}");
    }

    private void Log(string line) { _explore.Add(line); Console.WriteLine(line); }

    private async Task Frame()
    {
        await Settle();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!ReferenceEquals(_view!.Canvas.LastRenderedDocument, _editor.Session.Document) && DateTime.UtcNow < until)
        { _view.Canvas.InvalidateVisual(); await Task.Delay(30); }
    }

    private async Task Shot(string name)
    {
        await Frame();
        Capture(_view!, $"x{++_shot:00}-{name}");
    }

    private void ClickName(string name)
    {
        var button = _view!.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name)
            ?? throw new InvalidOperationException("no button " + name);
        if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled)
            throw new InvalidOperationException($"button {name} visible={button.IsEffectivelyVisible} enabled={button.IsEffectivelyEnabled}");
        button.Focus();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void TypeBox(string name, string text)
    {
        var box = _view!.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == name)
            ?? throw new InvalidOperationException("no box " + name);
        if (!box.IsEffectivelyVisible) throw new InvalidOperationException("box hidden " + name);
        box.Focus(); box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
    }

    private void CtrlZ(string focusedBox)
    {
        var box = _view!.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == focusedBox);
        box.Focus();
        // The key goes to the field itself, through its own tunnel and bubble handlers.
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Z, KeyModifiers = KeyModifiers.Control,
            Source = box };
        box.RaiseEvent(args);
        Log($"   Ctrl+Z in field: handled={args.Handled}");
    }

    private void ComboPick(string name, int index)
    {
        var box = _view!.GetVisualDescendants().OfType<ComboBox>().First(b => b.Name == name);
        if (!box.IsEffectivelyVisible) throw new InvalidOperationException("combo hidden " + name);
        box.SelectedIndex = index;
    }

    private void Key(Key key)
    {
        _view!.Canvas.Focus();
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        if (EditorKeyRouting.TryHandleKey(_window, _view, _editor.Shortcuts, args)) return;
        // The off-screen window never owns keyboard focus, so the canvas context is absent: run
        // the command the canvas-focused key resolves to.
        if (key == Avalonia.Input.Key.Enter) EditorKeyRouting.TryExecuteEditorCommand(_view, EditorShortcutSettings.Commands.FinishPath);
        else Log($"   key {key} not handled by editor routing");
    }

    private void ClickWorld(double x, double y)
    { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view!.Canvas.ClickForSmoke(p.X, p.Y); }

    private void DragWorld(double x0, double y0, double x1, double y1)
    {
        var a = _editor.Session.Viewport.WorldToScreen(x0, y0); var b = _editor.Session.Viewport.WorldToScreen(x1, y1);
        _view!.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, b.X, b.Y);
    }

    private async Task ExploreLarge()
    {
        _view!.Canvas.FitToCanvas(); await Frame();
        await Step("Лес по области (large)", () => ClickName("RegionalForestButton"));
        var big = new (double, double)[] { (1700, 500), (3300, 450), (3500, 1400), (3000, 2500), (1800, 2600), (1500, 1500) };
        foreach (var (x, y) in big) await Step($"large click {x},{y}", () => ClickWorld(x, y));
        await Task.WhenAny(_editor.PendingEditableAreaCount, Task.Delay(60000));
        await Step("count settled", () => { });
        await Shot("large-draft");
        if (_editor.DisplayedEditableArea is { } draft && _editor.Session.Document.Layers.OfType<ObjectLayer>().First(l => l.Name.StartsWith("Леса")).Id is var lid)
            for (var rep = 0; rep < 3; rep++)
            {
                var t = Stopwatch.StartNew(); var comp = PlacementAreaPlanner.InitializeComposition(draft); var tc = t.Elapsed.TotalMilliseconds;
                t.Restart(); var obs = new PlacementAreaObstacles(_editor.Session.Document, lid, comp.Id, comp.Sublayer, comp.LayoutVersion, comp); var to = t.Elapsed.TotalMilliseconds;
                t.Restart(); var pv = PlacementAreaPlanner.Preview(comp, null, obstacles: obs); var tp = t.Elapsed.TotalMilliseconds;
                t.Restart(); var pv2 = PlacementAreaPlanner.Preview(comp, null); var tp2 = t.Elapsed.TotalMilliseconds;
                t.Restart(); var sampler = PlacementAreaPlanner.Slots(comp, 0, 0).Count(); var ts = t.Elapsed.TotalMilliseconds;
                Log($"   profile: compose {tc:0} obstacles {to:0} preview+obs {tp:0} ({pv.Count}) preview {tp2:0} sampler {ts:0} ms");
            }
        await Step("density 100 during draft", () => TypeBox("EditableAreaDensityBox", "100"));
        await Task.WhenAny(_editor.PendingEditableAreaCount, Task.Delay(60000));
        await Step("Enter (place large)", () => Key(Avalonia.Input.Key.Enter));
        await Shot("large-placed");
        var area = _editor.SelectedEditableArea;
        Log($"   large area members {area?.MemberCount}");
        if (area is not null)
        {
            var n = area.Nodes[1]; var p = _editor.Session.Viewport.WorldToScreen(n.X, n.Y);
            var w = Stopwatch.StartNew();
            _view.Canvas.PressForSmoke(p.X, p.Y);
            var moves = new List<double>();
            for (var i = 1; i <= 20; i++)
            {
                var t = Stopwatch.StartNew(); _view.Canvas.MoveForSmoke(p.X + i * 4, p.Y + i * 3); moves.Add(t.Elapsed.TotalMilliseconds);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
            var r = Stopwatch.StartNew(); _view.Canvas.ReleaseForSmoke(p.X + 80, p.Y + 60);
            var releaseHandler = r.Elapsed.TotalMilliseconds;
            await Task.WhenAny(_editor.PendingEditableAreaWork, Task.Delay(60000));
            Log($"   release handler {releaseHandler:0} ms, background commit done after {r.Elapsed.TotalMilliseconds:0} ms");
            Log($"   vertex drag: moves max {moves.Max():0} ms avg {moves.Average():0} ms, release {r.Elapsed.TotalMilliseconds:0} ms, total {w.Elapsed.TotalMilliseconds:0} ms, undo {_editor.Session.UndoDepth}");
            await Frame(); await Shot("large-dragged");
            await Step("density 40 on large", () => TypeBox("EditableAreaDensityBox", "40"));
            await Step("Другой рисунок large", () => ClickName("AnotherEditableAreaArrangementButton"));
            await Step("Undo", () => ClickName("UndoButton"));
            await Step("Undo", () => ClickName("UndoButton"));
            await Step("Undo", () => ClickName("UndoButton"));
            await Step("Undo (placement)", () => ClickName("UndoButton"));
            await Step("Redo", () => ClickName("RedoButton"));
            area = _editor.SelectedEditableArea ?? _editor.EditableAreas.Last();
            _editor.SelectEditableArea(area.Id);
            // A real member of this area, to test object picking and detaching.
            var member = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                .FirstOrDefault(o => PlacementAreaOwnership.Owner(_editor.Session.Document, o.Id)?.Id == area.Id);
            if (member is not null)
            {
                await Step("Выбрать объект области", () => ClickName("SelectAreaObjectButton"));
                await Step("click member", () => ClickWorld(member.X + member.Width / 2, member.Y + member.Height / 2));
                await Step("Отделить выбранный объект", () => ClickName("DetachAreaObjectButton"));
            }
        }
        // After placing: click empty canvas while still in FillArea mode.
        await Step("click empty after placement", () => ClickWorld(600, 2900));
        await Step("click empty again", () => ClickWorld(700, 2950));
        await Step("mode: Один then click", () => { ClickName("StampModeOne"); });
        await Step("One click", () => ClickWorld(650, 2900));
        await Step("Esc", () => Key(Avalonia.Input.Key.Escape));
        await Step("Лес по области (draft then mode switch)", () => ClickName("RegionalForestButton"));
        await Step("draft 1", () => ClickWorld(600, 2800));
        await Step("draft 2", () => ClickWorld(900, 2800));
        await Step("mode: Серия mid draft", () => ClickName("StampModeSeries"));
        await Step("series click", () => ClickWorld(700, 2950));
        await Shot("large-end");
    }
}
