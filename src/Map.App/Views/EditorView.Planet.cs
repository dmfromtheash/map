using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Map.Export;
using SkiaSharp;

namespace Map.App.Views;

/// <summary>
/// R39: «Планета» - a window with the flag (a two-to-one map is a whole planet), the seamless
/// meridian, a globe turned by dragging, and the export in other projections.
/// </summary>
public sealed partial class EditorView
{
    private Window? _planetWindow;
    private Control? _planetContent;
    private readonly List<Action> _planetSyncs = [];
    private bool _syncingPlanet;

    private void ShowPlanet()
    {
        _planetWindow = ShowAuxiliaryWindow(_planetWindow, "Планета", BuildPlanetContent(), 660, 820, "PlanetWindow");
        SyncPlanet();
    }

    private Control BuildPlanetContent()
    {
        if (_planetContent is not null) return _planetContent;
        var enabled = Ui.PropertyToggle("PlanetEnabledToggle", "Карта — целая планета (2:1)");
        ToolTip.SetTip(enabled, "Левый край сходится с правым, верх — северный полюс, низ — южный.");
        var seamless = Ui.PropertyToggle("PlanetSeamlessToggle", "Бесшовный меридиан");
        ToolTip.SetTip(seamless, "За левым и правым краем карты видна другая сторона мира — бледно, только для сверки берегов.");
        enabled.IsCheckedChanged += (_, _) => { if (!_syncingPlanet) { _editor.SetPlanet(enabled.IsChecked == true, seamless.IsChecked == true); SyncPlanet(); } };
        seamless.IsCheckedChanged += (_, _) => { if (!_syncingPlanet) { _editor.SetPlanet(enabled.IsChecked == true, seamless.IsChecked == true); SyncPlanet(); } };
        var state = new TextBlock { Name = "PlanetStateText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");

        var globe = new GlobeView { Name = "PlanetGlobe", Width = 360, Height = 360, HorizontalAlignment = HorizontalAlignment.Center };
        var refresh = Ui.TextButton("PlanetGlobeRefreshButton", "Обновить глобус", () => globe.Load(_editor.RenderPlanetSource(2048)), "compact");
        ToolTip.SetTip(refresh, "Перерисовать глобус с текущей карты. Тяните глобус мышью, чтобы повернуть.");

        var projection = new ComboBox { Name = "PlanetProjectionBox", Width = 280, SelectedIndex = 1,
            ItemsSource = Enum.GetValues<PlanetProjection>().Select(PlanetProjections.Title).ToArray() };
        var width = new TextBox { Name = "PlanetWidthBox", Width = 80, Text = "3000" };
        var center = new TextBox { Name = "PlanetCenterBox", Width = 70, Text = "0" };
        ToolTip.SetTip(center, "Долгота в центре карты, градусы (−180…180).");
        var gores = new TextBox { Name = "PlanetGoresBox", Width = 50, Text = "12" };
        var save = Ui.AsyncButton("PlanetExportButton", "Сохранить PNG…", async () =>
        {
            var w = int.TryParse(width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 256, 8192) : 3000;
            var lon = double.TryParse(center.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, -180, 180) : 0;
            var n = int.TryParse(gores.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? Math.Clamp(g, 4, 36) : 12;
            var kind = (PlanetProjection)Math.Max(0, projection.SelectedIndex);
            await _editor.PickAndExportProjectionAsync(kind, w, kind == PlanetProjection.Orthographic ? globe.Longitude : lon,
                kind == PlanetProjection.Orthographic ? globe.Latitude : 0, n);
        }, "accent", "compact");
        var exportBlock = new StackPanel
        {
            Name = "PlanetExportBlock", Spacing = 6,
            Children =
            {
                projection,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = "Ширина", VerticalAlignment = VerticalAlignment.Center }, width,
                    new TextBlock { Text = "Центр, °", VerticalAlignment = VerticalAlignment.Center }, center,
                    new TextBlock { Text = "Клиньев", VerticalAlignment = VerticalAlignment.Center }, gores,
                } },
                Ui.Hint("Ортографическая сохраняется с тем поворотом, что у глобуса выше."),
                save,
            },
        };
        var planetOnly = new StackPanel { Spacing = 8, Children = { globe, refresh, Ui.Subgroup("Экспорт в проекции", exportBlock) } };

        void Sync()
        {
            _syncingPlanet = true;
            try
            {
                enabled.IsChecked = _editor.Planet.Enabled;
                seamless.IsChecked = _editor.Planet.SeamlessMeridian;
                seamless.IsEnabled = _editor.Planet.Enabled;
                planetOnly.IsVisible = _editor.Planet.Enabled;
                state.Text = _editor.Planet.Enabled ? "Планета: глобус, проекции и бесшовный меридиан включены."
                    : _editor.CanvasFitsPlanet ? "Холст 2:1 — карту можно отметить как планету."
                    : $"Холст {_editor.Session.Document.Canvas.Width}×{_editor.Session.Document.Canvas.Height}: для планеты нужен 2:1.";
                if (_editor.Planet.Enabled && !globe.HasSource) globe.Load(_editor.RenderPlanetSource(2048));
            }
            finally { _syncingPlanet = false; }
        }
        _planetSyncs.Add(Sync);
        _planetContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children = { enabled, seamless, state, planetOnly },
            },
        };
        Sync();
        return _planetContent;
    }

    private void SyncPlanet()
    {
        foreach (var sync in _planetSyncs) sync();
    }
}

/// <summary>R39: the planet on a sphere, turned by dragging (orthographic projection redrawn on the CPU).</summary>
internal sealed class GlobeView : Control
{
    private PlanetProjections.Source? _source;
    private WriteableBitmap? _bitmap;
    private Point? _drag;

    public double Longitude { get; private set; }
    public double Latitude { get; private set; } = 20;
    public bool HasSource => _source is not null;

    public void Load(SKBitmap planet)
    {
        using (planet) _source = new PlanetProjections.Source(planet);
        Redraw();
    }

    private void Redraw()
    {
        if (_source is null) return;
        var size = (int)Math.Max(64, Math.Min(Width, Height));
        using var globe = PlanetProjections.Project(_source, PlanetProjection.Orthographic, size, Longitude, Latitude);
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(globe.Width, globe.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using (var locked = _bitmap.Lock())
        {
            var bytes = globe.Bytes;
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, locked.Address, Math.Min(bytes.Length, locked.RowBytes * globe.Height));
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null) return;
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), new Rect(Bounds.Size));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } last) return;
        var point = e.GetPosition(this);
        Longitude = ((Longitude - ((point.X - last.X) * 0.5) + 540) % 360) - 180;
        Latitude = Math.Clamp(Latitude + ((point.Y - last.Y) * 0.5), -89, 89);
        _drag = point;
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    /// <summary>For the smoke: turn the globe as a drag would.</summary>
    public void Turn(double longitude, double latitude)
    {
        Longitude = longitude; Latitude = Math.Clamp(latitude, -89, 89);
        Redraw();
    }
}
