using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed class NativeRegionalRolesReport
{
    public string Observation { get; } = "Own off-screen Avalonia window, ShowActivated=false; procedural PNG user sets, real regional buttons and canvas router. No system input or screen capture.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public List<string> Frames { get; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 65 && Checks.Values.All(v => v) && Frames.Count >= 7;
}

internal static class NativeRegionalRolesSmoke
{
    public static NativeRegionalRolesReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeRegionalRolesReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            RegionalRolesApplication.Options = options; RegionalRolesApplication.Completion = completion;
            try { AppBuilder.Configure<RegionalRolesApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
    }
}

public sealed class RegionalRolesApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeRegionalRolesReport> Completion = null!;
    private readonly NativeRegionalRolesReport _report = new();
    private readonly Dictionary<AssetRole, string> _sets = [];
    private readonly Dictionary<AssetRole, string> _archived = [];
    private readonly LayerId _objects = LayerId.New();
    private string _workspace = "";
    private AssetCatalog _catalog = null!;
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;

    public override void Initialize()
    {
        var production = new global::Map.App.App(); production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray(); production.Styles.Clear(); Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "regional-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _window = new Window { Width = 1440, Height = 1000, Title = "Map — R11 роли (procedural smoke)",
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000) };
        OpenEditor(Valley());
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex)
            {
                _report.Failure = ex.ToString();
                try { ShotEditor("failure"); } catch { /* Keep the original failure. */ }
            }
            finally
            {
                try
                {
                    await CloseEditor(); await _catalog.DisposeAsync(); _window.Close();
                    Directory.Delete(_workspace, recursive: true);
                    Check("isolated-workspace-deleted", !Directory.Exists(_workspace));
                }
                catch (Exception ex) { _report.Failure ??= ex.ToString(); }
                Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private MapDocument Valley(bool cliffs = true)
    {
        var tiles = TileMap.Empty(1);
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
            tiles = tiles.With(new(x, y), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        var land = new LandLayer(LayerId.New(), "Равнина", true, false, 1, tiles, new() { FillColor = "#FFD4D7B0" });
        var plateau = new LandLayer(LayerId.New(), "Уступ для склона", true, false, 1,
            TileMap.Empty(1).With(new(3, 1), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray()),
            new() { FillColor = "#FFB3B493" }) { Level = 3 };
        var river = new River { Name = "Исходная река", Style = RiverStyle.Natural(),
            Channels = [RiverEditor.NewChannel([new(350, 0), new(350, 1100)], 60)] };
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), land };
        if (cliffs) layers.Add(plateau);
        layers.Add(new ObjectLayer(_objects, "Объекты и реки", true, false, 1, []) { Rivers = [river] });
        return new(DocumentId.New(), cliffs ? "R11 — долина" : "R11 — без обрывов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 1100, "px", "#FFE9E1CC"), layers);
    }

    // Diagnostic geometry only: no product artwork or external fixtures are installed.
    private void BuildLibrary()
    {
        foreach (var role in new[] { AssetRole.Tree, AssetRole.Spruce, AssetRole.Bush, AssetRole.Peak, AssetRole.Slope, AssetRole.House, AssetRole.Tower })
        foreach (var archived in new[] { false, true })
        {
            var path = Path.Combine(_workspace, role + (archived ? "-archived" : "") + ".png");
            using var surface = SKSurface.Create(new SKImageInfo(48, 64))!;
            var canvas = surface.Canvas; canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Color = archived ? SKColors.Magenta : role switch
            {
                AssetRole.Tree => SKColors.ForestGreen, AssetRole.Spruce => SKColors.Teal, AssetRole.Bush => SKColors.OliveDrab,
                AssetRole.Peak => SKColors.SlateBlue, AssetRole.Slope => SKColors.Sienna, AssetRole.House => SKColors.OrangeRed,
                _ => SKColors.SteelBlue,
            } };
            void Polygon(params SKPoint[] points)
            {
                using var shape = new SKPath(); shape.AddPoly(points, close: true); canvas.DrawPath(shape, paint);
            }
            switch (role)
            {
                case AssetRole.Tree: canvas.DrawRect(20, 25, 8, 32, paint); canvas.DrawCircle(24, 22, 19, paint); break;
                case AssetRole.Spruce: canvas.DrawRect(21, 33, 6, 24, paint); Polygon(new(24, 3), new(3, 48), new(45, 48)); break;
                case AssetRole.Bush: canvas.DrawOval(new SKRect(3, 29, 45, 55), paint); break;
                case AssetRole.Peak: Polygon(new(3, 56), new(24, 4), new(45, 56)); break;
                case AssetRole.Slope: Polygon(new(3, 55), new(13, 24), new(34, 38), new(45, 55)); break;
                case AssetRole.House: canvas.DrawRect(9, 27, 30, 29, paint); Polygon(new(3, 28), new(24, 6), new(45, 28)); break;
                case AssetRole.Tower: canvas.DrawRect(13, 16, 22, 40, paint); canvas.DrawRect(8, 9, 32, 10, paint); break;
            }
            using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
            var set = _catalog.ImportSet([path], role + (archived ? " — архив" : " — свой набор"), new(), DateTimeOffset.UnixEpoch);
            _catalog.SetVariantPresentation(set.VariantIds[0], role.ToString(), .5, .85, .09375);
            _catalog.SetPlacementDefaults(set.Id, new() { Role = role, AllowedRotations = [0], CanFlip = false, DepthSort = true });
            _catalog.SetSetArchived(set.Id, archived);
            (archived ? _archived : _sets).Add(role, set.Id);
        }
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel();
        Check("procedural-user-library-seven-roles-and-archives", _catalog.Sets.Count == 14 && _catalog.Records.Count == 14
            && _sets.All(p => _catalog.FindSet(p.Value)?.Placement?.Role == p.Key)
            && _archived.Values.All(id => _catalog.FindSet(id)!.Archived));
    }

    private void OpenEditor(MapDocument document, string? path = null, bool withCatalog = true)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "roles.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: withCatalog ? _catalog : null, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        _editor.SelectLayer(_objects);
    }

    private async Task CloseEditor()
    {
        _editor.CancelPlacement(); _editor.CancelEditableAreaGesture(); _editor.CancelEditablePathGesture();
        await _editor.PendingEditableAreaCount; await _editor.PendingEditableAreaPreview; await _editor.PendingEditableAreaWork;
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
    }

    private ObjectLayer Layer() => (ObjectLayer)_editor.Session.Document.FindLayer(_objects)!;
    private SceneObject[] Members(PlacementArea area) => Layer().Objects.Where(o => area.Members.Any(m => m.ObjectId == o.Id)).ToArray();

    private async Task Execute()
    {
        await Settle(); BuildLibrary(); await _catalog.FlushMetadataAsync();
        _view.Canvas.FitToCanvas(); await Settle();
        _editor.ActiveTool = EditorTool.Stamp; await Settle();
        // Prefer an archived selection deliberately. Every tool must fall back to an active role.
        foreach (var (role, mode) in new[] { (AssetRole.Tree, "Forest"), (AssetRole.Peak, "Ridge"), (AssetRole.House, "Settlement") })
        {
            _editor.SelectAssetSet(_archived[role]); var before = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
            await Click("Regional" + mode + "Button");
            Check(mode + "-excludes-archived-selection", _sets.ContainsValue(_editor.SelectedSetId!)
                && !_catalog.FindSet(_editor.SelectedSetId!)!.Archived && ReferenceEquals(before, _editor.Session.Document)
                && depth == _editor.Session.UndoDepth);
            _editor.CancelPlacement();
        }

        var forest = await Create(AssetRole.Tree, "Forest", [(40, 40), (660, 40), (660, 840), (40, 840)]);
        var primary = Layer().PlacementAreas.Single(a => a.Id == forest);
        CheckDensity(primary);
        var water = new PlacementAreaRuleCheck(_editor.Session.Document, new() { AvoidWater = true });
        Check("forest-no-tree-on-existing-water", Members(primary).Length > 0 && Members(primary).All(o => !water.Blocks(o)));
        await Shot("forest-river-before");
        await RecalculateForest(primary.Id);

        await Create(AssetRole.Spruce, "Forest", [(40, 880), (300, 880), (300, 1050), (40, 1050)]);
        await Create(AssetRole.Bush, "Forest", [(390, 880), (650, 880), (650, 1050), (390, 1050)]);
        await Create(AssetRole.Peak, "Ridge", [(740, 100), (930, 140), (1140, 100)], path: true);
        var slope = await Create(AssetRole.Slope, "Ridge", [(710, 220), (1150, 220), (1150, 580), (710, 580)]);
        var slopeArea = Layer().PlacementAreas.Single(a => a.Id == slope);
        var ecology = new PlacementAreaEcology(_editor.Session.Document, slopeArea, forest: false);
        Check("slope-only-at-existing-cliff", slopeArea.MemberCount > 0 && slopeArea.Pattern == PlacementAreaPattern.Ridge
            && slopeArea.Members.All(m => !ecology.Blocks(m.Slot, Layer().Objects.Single(o => o.Id == m.ObjectId))));
        await Shot("ridge");
        await Create(AssetRole.House, "Settlement", [(740, 650), (930, 650), (930, 940), (740, 940)]);
        await Create(AssetRole.Tower, "Settlement", [(960, 650), (1150, 650), (1150, 940), (960, 940)]);
        await Shot("settlement");

        await Click("SaveButton"); var file = Path.Combine(_workspace, "roles.mapproj");
        await Until(() => File.Exists(file) && !_editor.Session.IsDirty, "save");
        using (var zip = ZipFile.OpenRead(file))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
        {
            var schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
            _report.Notes["schema"] = schema.ToString(CultureInfo.InvariantCulture); Check("save-current-schema-v30", schema == 30);
        }
        var saved = Layer(); var reopened = MapProjectStore.Open(file).Document;
        await CloseEditor(); OpenEditor(reopened, file, withCatalog: false); await Settle(); _view.Canvas.FitToCanvas();
        Check("save-reopen-exact-objects-without-catalog", saved.Objects.SequenceEqual(Layer().Objects));
        Check("save-reopen-exact-areas-and-paths-without-catalog", Same(saved.PlacementAreas, Layer().PlacementAreas)
            && Same(saved.PlacementPaths, Layer().PlacementPaths) && Same(saved.Rivers, Layer().Rivers));
        foreach (var area in Layer().PlacementAreas) area.ValidateMembers();
        foreach (var path in Layer().PlacementPaths) path.ValidateMembers();
        Check("reopened-member-links-valid", Layer().PlacementAreas.Length == 6 && Layer().PlacementPaths.Length == 1);
        // The map's linked images still use this isolated catalog. Reattach it for visual review;
        // the checks above deliberately establish that area/object data itself needs no catalog.
        await CloseEditor(); OpenEditor(reopened, file); await Settle(); _view.Canvas.FitToCanvas();
        await Shot("whole-map-reopened"); ShotEditor("editor-reopened");

        await CloseEditor(); OpenEditor(Valley(cliffs: false)); await Settle(); _view.Canvas.FitToCanvas();
        var flat = await Create(AssetRole.Slope, "Ridge", [(710, 220), (1150, 220), (1150, 580), (710, 580)], empty: true);
        var flatArea = Layer().PlacementAreas.Single(a => a.Id == flat);
        Check("flat-map-slope-empty-without-exception", flatArea.MemberCount == 0 && flatArea.RuleBlocked.Count > 0
            && Layer().Objects.Count == 0 && _editor.Session.LastResourceError is null);
    }

    private async Task<Guid> Create(AssetRole role, string mode, (double X, double Y)[] points, bool path = false, bool empty = false)
    {
        var label = role + (empty ? "-flat" : "");
        _editor.ActiveTool = EditorTool.Stamp; await Settle();
        _editor.SelectLayer(_objects); _editor.SelectAssetSet(_sets[role]);
        var before = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        await Click("Regional" + mode + "Button");
        Check(label + "-real-button-starts-right-role", _editor.SelectedSetId == _sets[role]
            && _editor.StampPlacementMode == (path ? StampPlacementMode.Path : StampPlacementMode.FillArea));
        foreach (var p in points) { Point(p.X, p.Y); await Settle(); }
        Require(path ? _editor.IsCreatingEditablePath : _editor.IsCreatingEditableArea, label + " did not start a canvas draft");
        if (!path) _editor.SetEditableAreaSeed(Options.Seed);
        await Idle();
        await Click(path ? "FinishEditablePathButton" : "FinishEditableAreaButton"); await Idle();
        var id = path ? _editor.SelectedEditablePath!.Id : _editor.SelectedEditableArea!.Id;
        var area = path ? null : Layer().PlacementAreas.Single(a => a.Id == id);
        var ridge = path ? Layer().PlacementPaths.Single(p => p.Id == id) : null;
        area?.ValidateMembers(); ridge?.ValidateMembers();
        var ids = path ? ridge!.Members.Select(m => m.ObjectId).ToHashSet() : area!.Members.Select(m => m.ObjectId).ToHashSet();
        var members = Layer().Objects.Where(o => ids.Contains(o.Id)).ToArray();
        var shas = _catalog.FindSet(_sets[role])!.VariantIds.Select(v => _catalog.FindVariant(v)!.Sha256).ToHashSet();
        Check(label + "-only-right-active-set-and-role", (area?.AssetSetId ?? ridge?.AssetSetId) == _sets[role]
            && (empty ? members.Length == 0 : members.Length > 0) && members.All(o => o.Stamp is { } s && shas.Contains(s.AssetSha256))
            && !_catalog.FindSet(_sets[role])!.Archived);
        _report.Notes[label + "-members"] = members.Length.ToString(CultureInfo.InvariantCulture);
        var after = _editor.Session.Document;
        Check(label + "-creation-one-undo", _editor.Session.UndoDepth == depth + 1);
        await Click("UndoButton");
        Check(label + "-undo-restores-document", ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == depth);
        await Click("RedoButton");
        Check(label + "-redo-restores-document", ReferenceEquals(after, _editor.Session.Document) && _editor.Session.UndoDepth == depth + 1);
        return id;
    }

    private void CheckDensity(PlacementArea area)
    {
        // Equal-width strips, away from the polygon's edge: compare actual materialized trees per
        // unit area, plus kept/candidate rates to distinguish ecology from the natural pattern.
        var near = 0; var far = 0; var nearTotal = 0; var farTotal = 0;
        var kept = area.Members.Select(m => m.Slot).ToHashSet(); var sampler = new PlacementAreaSampler(area);
        foreach (var span in PlacementAreaGeometry.Coverage(area.Nodes))
        for (var x = span.MinX; x <= span.MaxX; x++)
        foreach (var slot in sampler.Slots(x, span.Y))
        {
            var foot = PlacementAreaRuleCheck.Foot(sampler.Item(slot));
            if (foot.Y is < 180 or > 700) continue;
            var distance = Math.Abs(foot.X - 350);
            if (distance is > 45 and < 85) { nearTotal++; if (kept.Contains(slot)) near++; }
            if (distance is > 150 and < 190) { farTotal++; if (kept.Contains(slot)) far++; }
        }
        _report.Notes["forest-density-near-far"] = $"equal-area tree counts {near}/{far}; candidates {nearTotal}/{farTotal}; kept rates {(double)near / Math.Max(1, nearTotal):F3}/{(double)far / Math.Max(1, farTotal):F3}";
        Check("forest-denser-near-water-than-far", nearTotal >= 20 && farTotal >= 20 && near > far
            && (double)near / nearTotal > (double)far / farTotal);
    }

    private async Task RecalculateForest(Guid id)
    {
        var old = Members(Layer().PlacementAreas.Single(a => a.Id == id));
        _editor.ActiveTool = EditorTool.River; await Settle();
        await Click("StartRiverButton"); _editor.NewRiverWidth = 90;
        Point(80, 440); Point(620, 440); await Settle(); await Click("FinishRiverButton");
        Check("new-river-drawn-through-real-controls", Layer().Rivers.Length == 2);
        _editor.ActiveTool = EditorTool.Stamp; _editor.SelectEditableArea(id); await Settle();
        var before = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        var water = new PlacementAreaRuleCheck(before, new() { AvoidWater = true });
        var flooded = old.Where(water.Blocks).Select(o => o.Id).ToHashSet();
        Check("new-river-covers-existing-trees-before-recalculation", flooded.Count > 0);
        await Shot("forest-river-new-water-before-refresh");
        await Click("EditableAreaRefreshRulesButton"); await Idle();
        var area = Layer().PlacementAreas.Single(a => a.Id == id); var current = Members(area);
        Check("recalculate-removes-trees-from-new-water", current.Length > 0 && current.All(o => !water.Blocks(o))
            && current.All(o => !flooded.Contains(o.Id)) && area.Rules is { Revision: 1, Any: false });
        Check("recalculate-one-undo-step", _editor.Session.UndoDepth == depth + 1);
        await Shot("forest-river-after");
        var after = _editor.Session.Document;
        await Click("UndoButton");
        Check("recalculate-one-undo-restores-exact-trees-and-river", ReferenceEquals(before, _editor.Session.Document)
            && old.SequenceEqual(Members(Layer().PlacementAreas.Single(a => a.Id == id))) && Layer().Rivers.Length == 2);
        await Click("RedoButton");
        Check("recalculate-redo-restores-result", ReferenceEquals(after, _editor.Session.Document));
    }

    // Compare every serialized field, retaining array order for nodes/variants/objects. Hash sets
    // have no order; normalize just those properties before comparing a disk round trip.
    private static bool Same<T>(T expected, T actual)
    {
        static void Normalize(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj.ToArray())
                {
                    Normalize(value);
                    if (key is "Exclusions" or "OccupiedSlots" or "RuleSlots" && value is JsonArray array)
                        obj[key] = new JsonArray(array.OrderBy(v => v!.ToJsonString(), StringComparer.Ordinal).Select(v => v!.DeepClone()).ToArray());
                }
            }
            else if (node is JsonArray array) foreach (var value in array) Normalize(value);
        }
        var a = JsonSerializer.SerializeToNode(expected); var b = JsonSerializer.SerializeToNode(actual);
        Normalize(a); Normalize(b); return JsonNode.DeepEquals(a, b);
    }

    private void Point(double x, double y)
    {
        var p = _editor.Session.Viewport.WorldToScreen(x, y);
        Require(p.X >= 0 && p.Y >= 0 && p.X < _view.Canvas.Bounds.Width && p.Y < _view.Canvas.Bounds.Height, "world point outside canvas");
        _view.Canvas.ClickForSmoke(p.X, p.Y);
    }

    private T Find<T>(string name) where T : Control
    {
        NativeEditorScenarioApplication.RevealTab(_view, name);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
            foreach (var expander in _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray()) expander.IsExpanded = true;
            _view.UpdateLayout();
        }
        throw new InvalidOperationException(name + " is not in the panel");
    }

    private async Task Click(string name)
    {
        var button = Find<Button>(name); button.BringIntoView(); await Settle();
        Require(button.IsEffectivelyVisible && button.IsEnabled, name + " unavailable: " + _editor.StatusDetail);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
    }

    private async Task Idle()
    {
        await _editor.PendingEditableAreaCount; await _editor.PendingEditableAreaPreview; await _editor.PendingEditableAreaWork;
        await Until(() => !_editor.IsEditableAreaBusy, "area work"); await Settle();
    }

    private async Task Shot(string name)
    {
        await Idle();
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = long.MaxValue;
        void Rendered(object? sender, EventArgs args)
        {
            if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)
                && _view.Canvas.CompletedSceneRevision >= revision && !_view.Canvas.LastFrameFastTerrain
                && !_view.Canvas.LastTerrainRefinementPending && !_view.Canvas.LastDetailTilesPending) frame.TrySetResult();
        }
        _view.Canvas.FrameRendered += Rendered; revision = _view.Canvas.RequestFrameForSmoke();
        try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { _view.Canvas.FrameRendered -= Rendered; }
        using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        Check("frame-" + name + "-native-complete", _view.Canvas.LastFrameHadGpuContext
            && _view.Canvas.LastRenderStats.MissingAssets == 0 && !_view.Canvas.LastFrameFastTerrain
            && !_view.Canvas.LastTerrainRefinementPending && !_view.Canvas.LastDetailTilesPending);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        File.WriteAllBytes(path, png.ToArray()); _report.Frames.Add(path);
    }

    private void ShotEditor(string name)
    {
        _view.UpdateLayout(); var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)_view.Bounds.Width, (int)_view.Bounds.Height));
        bitmap.Render(_view); bitmap.Save(path, PngBitmapEncoderOptions.Default); _report.Frames.Add(path);
    }

    private void Check(string key, bool value)
    { _report.Checks[key] = value; Console.WriteLine((value ? "ok      " : "FAILED  ") + key); }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(60); }
    private static async Task Until(Func<bool> condition, string operation)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(operation); await Task.Delay(60); } }

    private sealed class LocalPicker(string path) : IFilePicker
    { public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path); public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path); }
    private sealed class LocalRecent : IRecentMaps
    { public IReadOnlyList<RecentMapItem> Load() => []; public void Remove(string path) { }
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { } }
    private sealed class LocalLog : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + " " + exception?.Message); }
    private sealed class LocalPrompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
