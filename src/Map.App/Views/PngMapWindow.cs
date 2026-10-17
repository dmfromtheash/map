using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Map.App.Controls;
using Map.App.Services;
using Map.Export;

namespace Map.App.Views;

/// <summary>Native exported-image viewer; it never loads or edits a Map document.</summary>
public sealed class PngMapWindow : Window
{
    private readonly TextBlock _status = new() { Name = "PngViewStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _statistics = new() { Name = "PngViewStatistics", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Name = "PngViewProgress", Minimum = 0, Maximum = 1, Height = 5 };
    private readonly Button _open, _cancel;
    private readonly string _cacheRoot;
    private CancellationTokenSource? _cancellation;
    private bool _closed;
    private PngTilePyramid? _pyramid;
    private IDisposable? _cacheUsage;
    public PngMapView ImageView { get; } = new();
    public bool IsPreparing { get; private set; }
    public string StatusText => _status.Text ?? "";
    public PngTilePyramid? Pyramid => _pyramid;

    public PngMapWindow(string? cacheRoot = null)
    {
        Name = "PngMapWindow"; Title = "Map · Просмотр PNG";
        Width = 1280; Height = 820; MinWidth = 760; MinHeight = 480;
        _cacheRoot = cacheRoot ?? Path.Combine(TestMode.ProfileRootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Map"), "ImageViewCache");
        _open = Ui.AsyncButton("OpenPngViewButton", "Открыть PNG…", PickAsync, "accent");
        _cancel = Ui.TextButton("CancelPngViewButton", "Отменить подготовку", () => _cancellation?.Cancel(), "ghost");
        _cancel.IsVisible = false;
        var toolbar = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children =
        {
            _open,
            Ui.TextButton("FitPngViewButton", "Вся карта", ImageView.Fit, "ghost"),
            Ui.TextButton("NativePngViewButton", "1:1", () => ImageView.ZoomAt(Center(), 1), "accent"),
            Ui.TextButton("ZoomOutPngViewButton", "−", () => ImageView.ZoomAt(Center(), ImageView.Zoom / 1.5), "ghost"),
            Ui.TextButton("ZoomInPngViewButton", "+", () => ImageView.ZoomAt(Center(), ImageView.Zoom * 1.5), "ghost"),
            _cancel,
        } };
        _statistics.Foreground = new SolidColorBrush(Color.Parse("#D4AE62"));
        _status.Text = "Откройте PNG из Map. Колесо — приближение, перетаскивание — перемещение.";
        Content = Ui.WorkspaceBackdrop(new DockPanel { Children =
        {
            new Border { Padding = new Thickness(16, 12), Child = toolbar }.Classed("surface-raised").Docked(Dock.Top),
            new Border { Padding = new Thickness(16, 10), Child = new StackPanel { Spacing = 6, Children =
            { _progress, _statistics, _status } } }.Classed("dialog-footer").Docked(Dock.Bottom),
            ImageView,
        } });
        ImageView.ViewChanged += UpdateStatistics;
        Closed += async (_, _) =>
        {
            _closed = true; _cancellation?.Cancel(); ImageView.Dispose();
            var usage = _cacheUsage; _cacheUsage = null;
            try { await ImageView.LoadingTask; }
            finally { usage?.Dispose(); TrimCache(); }
        };
    }

    public static PngMapWindow ShowFor(Window? owner, string? sourcePath = null)
    {
        var view = new PngMapWindow();
        if (TestMode.Enabled) { view.ShowActivated = false; view.ShowInTaskbar = false; if (owner is not null) view.Position = owner.Position; }
        if (sourcePath is not null) view.Opened += async (_, _) => await view.OpenImageAsync(sourcePath);
        if (owner is null) view.Show(); else view.Show(owner);
        return view;
    }

    public async Task OpenImageAsync(string path)
    {
        if (IsPreparing || _closed) return;
        _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        IsPreparing = true; _open.IsEnabled = false; _cancel.IsVisible = true;
        _progress.IsVisible = true; _progress.Value = 0;
        _status.Text = "Первое открытие: готовим данные чёткого зума на диске. Это может занять время. Исходный PNG остаётся без изменений.";
        var progress = new Progress<ExportProgress>(p => { if (!_closed) { _progress.Value = p.Fraction; _status.Text = p.Message; } });
        IDisposable? usage = null;
        try
        {
            usage = await PngViewCache.AcquireAsync(_cacheRoot, _cancellation.Token);
            var pyramid = await PngTilePyramid.OpenAsync(path, _cacheRoot, progress, _cancellation.Token);
            if (_closed) return;
            _pyramid = pyramid; ImageView.SetPyramid(pyramid);
            _cacheUsage?.Dispose(); _cacheUsage = usage; usage = null;
            Title = "Map · " + Path.GetFileName(path);
            _status.Text = "Колесо — приближение · перетаскивание — перемещение · 1:1 — исходные пиксели. Временные данные очищаются автоматически после закрытия просмотра.";
            UpdateStatistics();
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "Подготовка отменена. Исходный PNG сохранён."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ExportValidationException or OutOfMemoryException or InvalidOperationException)
        { if (!_closed) _status.Text = "Не удалось подготовить просмотр: " + ex.Message; }
        finally
        {
            usage?.Dispose();
            IsPreparing = false;
            if (_closed) TrimCache();
            if (!_closed) { _open.IsEnabled = true; _cancel.IsVisible = false; _progress.IsVisible = false; }
        }
    }

    private void TrimCache() => _ = Task.Run(() => PngViewCache.TrimWhenIdle(_cacheRoot));

    private async Task PickAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Открыть PNG из Map", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PNG из Map") { Patterns = ["*.png"] }] });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await OpenImageAsync(path);
    }
    private Point Center() => new(ImageView.Bounds.Width / 2, ImageView.Bounds.Height / 2);
    private void UpdateStatistics()
    {
        if (_pyramid is not { } p) return;
        _statistics.Text = $"{p.Width:N0} × {p.Height:N0} пикс.   ·   {ImageView.Zoom:P1}   ·   Изображения в памяти: {ImageView.CachedPixelBytes / 1048576d:N0} / 128 МиБ";
        if (!string.IsNullOrEmpty(ImageView.Error)) _status.Text = ImageView.Error;
    }
}
