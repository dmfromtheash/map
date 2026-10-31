using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Map.App.Views;

/// <summary>
/// R38: «Изометрия» - the map in isometry: four sides to look from, the height of the buildings,
/// the picture refreshed on demand and saved as PNG.
/// </summary>
public sealed partial class EditorView
{
    private Window? _isometricWindow;
    private Control? _isometricContent;
    private Image? _isometricImage;

    private void ShowIsometric()
    {
        _isometricWindow = ShowAuxiliaryWindow(_isometricWindow, "Изометрия", BuildIsometricContent(), 900, 760, "IsometricWindow");
        RefreshIsometric();
    }

    private void RefreshIsometric()
    {
        if (_isometricImage is null) return;
        var old = _isometricImage.Source as Bitmap;
        using var stream = new MemoryStream(_editor.RenderIsometricPreview(1400));
        _isometricImage.Source = new Bitmap(stream);
        old?.Dispose();
    }

    private Control BuildIsometricContent()
    {
        if (_isometricContent is not null) return _isometricContent;
        _isometricImage = new Image { Name = "IsometricPreview", Stretch = Stretch.Uniform, MaxHeight = 520 };
        IReadOnlyList<Button> sideButtons = [];
        void MarkSide() { foreach (var b in sideButtons) Ui.SetClass(b, "active", b.Name == "IsometricSide" + _editor.IsometricSettings.Turn.ToString(CultureInfo.InvariantCulture)); }
        var (sides, buttons) = Ui.Segmented("IsometricSide", [("0", "С юга", null), ("1", "С запада", null), ("2", "С севера", null), ("3", "С востока", null)], key =>
        {
            // Kept in the map (plan 2.3): one undo step, and a reopened map looks from the same side.
            _editor.SetIsometricTurn(int.Parse(key, CultureInfo.InvariantCulture));
            MarkSide();
            RefreshIsometric();
        });
        sideButtons = buttons;
        MarkSide();
        // Undo, redo and another map bring the side back from the document.
        _appearanceSyncs.Add(MarkSide);
        ToolTip.SetTip(sides, "С какой стороны смотреть на карту.");
        // The preview follows the drag; letting go keeps the height in the map as one undo step.
        var height = ValueRow("IsometricHeightSlider", "IsometricHeightBox", null, "Высота", 50, 250, "%",
            () => _editor.IsometricSettings.HeightScale * 100,
            (value, commit) => { _editor.SetIsometricHeight(value / 100, commit); RefreshIsometric(); },
            step: 5, defaultValue: 100, description: "Высота домов, стен и башен.");
        var width = new TextBox { Name = "IsometricWidthBox", Width = 80, Text = "3000" };
        var save = Ui.AsyncButton("IsometricExportButton", "Сохранить PNG…", async () =>
        {
            var w = int.TryParse(width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 3000;
            await _editor.PickAndExportIsometricAsync(w);
        }, "accent", "compact");
        var refresh = Ui.TextButton("IsometricRefreshButton", "Обновить", RefreshIsometric, "compact");
        ToolTip.SetTip(refresh, "Перерисовать после правок карты.");
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        _isometricContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Дома, храмы, ратуша, стены и башни встают объёмом, площади, вода и улицы лежат на земле. Пока это простые формы — до изометрического набора художника."),
                    sides,
                    height,
                    refresh,
                    new Border { Child = _isometricImage }.Classed("group"),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = "Ширина PNG", VerticalAlignment = VerticalAlignment.Center }, width, save,
                    } },
                },
            },
        };
        return _isometricContent;
    }
}
