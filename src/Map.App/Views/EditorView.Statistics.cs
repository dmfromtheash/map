using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Map.Core.Documents;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private readonly TextBlock _mapStatistics = Status("MapStatisticsText");
    private readonly TextBlock _mapObjectCount = Status("MapObjectCountText");
    private readonly TextBlock _frameRateStatus = Status("MapFpsText");
    private readonly TextBlock _statisticsDetails = new()
    {
        Name = "MapStatisticsDetails", TextWrapping = TextWrapping.Wrap,
    };
    private readonly DispatcherTimer _statisticsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Flyout _statisticsFlyout = null!;

    private Control BuildStatisticsButton()
    {
        _mapStatistics.Classes.Add("role-caption");
        _mapObjectCount.Classes.Add("role-caption");
        _mapStatistics.Foreground = Brush.Parse("#E7C982");
        _mapObjectCount.Foreground = Brush.Parse("#C2AE8E");
        _statisticsFlyout = new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            Content = new ScrollViewer
            {
                MaxHeight = 340, Width = 350,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Статистика карты", FontWeight = FontWeight.SemiBold },
                        _statisticsDetails,
                        new TextBlock
                        {
                            Text = "Счётчик включает объекты во всех слоях, в том числе скрытые. " +
                                   "Составной объект считается одним. Кисть, земля и заметки в это число не входят.\n\n" +
                                   "FPS — частота обновления холста во время работы. В покое карта не перерисовывается постоянно.",
                            TextWrapping = TextWrapping.Wrap,
                        }.Classed("muted", "role-caption"),
                    },
                },
            },
        };
        var readouts = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _mapStatistics, _mapObjectCount, _frameRateStatus, _saveState },
        };
        foreach (var item in readouts.Children)
            item.Margin = new Thickness(7, 2);
        var button = new Button
        {
            Name = "MapStatisticsButton", Content = readouts, Flyout = _statisticsFlyout,
            Background = Brush.Parse("#2A2019"), BorderBrush = Brush.Parse("#6E5532"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(4, 1), MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, "Статистика карты: размер, объекты, сохранение и история");
        ToolTip.SetTip(button, "Открыть статистику карты");
        _statisticsFlyout.Opening += (_, _) => SyncMapStatistics();
        return button;
    }

    private void InitializeStatistics()
    {
        _statisticsTimer.Tick += (_, _) => SyncFrameStatistics();
        AttachedToVisualTree += (_, _) => _statisticsTimer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _statisticsTimer.Stop();
            _statisticsFlyout.Hide();
        };
        SyncFrameStatistics();
    }

    private void SyncFrameStatistics()
    {
        var frame = _canvas.ReadFrameStatistics();
        // The rate is shown only while the map is actually moving. At rest two stray repaints
        // used to read as "FPS 1" in an alarm colour, which looked like a failure.
        var moving = frame.Fps is not null && _canvas.RecentFrameCount() >= 6;
        _frameRateStatus.IsVisible = moving;
        _frameRateStatus.Text = moving ? $"FPS {frame.Fps:0}" : "FPS — покой";
        _frameRateStatus.Foreground = Brush.Parse(moving && frame.Fps < 20 ? "#E5907A" : "#C2AE8E");
        ToolTip.SetTip(_frameRateStatus, $"Последняя отрисовка холста: {frame.RenderMilliseconds:0.0} мс. " +
            "FPS измеряется по завершённым обновлениям карты; это не частота монитора.");
        if (_statisticsFlyout.IsOpen) SyncMapStatistics();
    }

    private void SyncMapStatistics()
    {
        var document = _editor.Session.Document;
        // Only layer totals are read. No traversal of a large object collection on UI refresh.
        var objects = document.Layers.OfType<ObjectLayer>().Sum(layer => (long)layer.Objects.Count);
        var canvas = document.Canvas;
        var unit = canvas.Unit == "px" ? "пкс" : canvas.Unit;
        _mapStatistics.Text = $"{canvas.Width:N0}×{canvas.Height:N0} {unit}";
        _mapObjectCount.Text = $"Объектов {objects:N0}";
        _saveState.Foreground = Brush.Parse(_editor.SaveState switch
        {
            Map.Presentation.ViewModels.SaveState.Saved => "#86C49A",
            Map.Presentation.ViewModels.SaveState.SaveFailed => "#E5907A",
            _ => "#E3B565",
        });
        _statisticsDetails.Text = $"Размер: {canvas.Width:N0} × {canvas.Height:N0} {unit}\n" +
            $"Объектов: {objects:N0}\nСлоёв: {document.Layers.Count}\n" +
            $"Заметок: {document.Notes.Count} · Версий: {document.Checkpoints.Count}\n" +
            $"Отмена: {_editor.Session.UndoDepth} · Повтор: {_editor.Session.RedoDepth}\n\n" +
            $"{_editor.SaveSummary}\nАвтосохранение: {(_editor.PrimaryAutosaveEnabled ? "включено" : "выключено")}\n{_editor.AutosaveSummary}\n" +
            (_editor.LastPrimarySaveUtc is { } saved ? $"Последнее сохранение: {saved.ToLocalTime():g}\n" : "") +
            (string.IsNullOrWhiteSpace(_editor.DocumentPath) ? "Файл ещё не выбран" : $"Файл: {Path.GetFileName(_editor.DocumentPath)}");
    }
}
