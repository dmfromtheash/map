using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.App.Services;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    // ========================================================== quick property strip

    /// <summary>
    /// The properties a selected object needs constantly, as a strip across the top of the editor
    /// rather than as the first rows of a panel that has to be scrolled.
    /// <para>
    /// The owner's reference shows these next to each other above the map, and that is the point:
    /// layer, level, scale, opacity and rotation are read and changed far more often than anything
    /// else, so they must be where the eye already is. The detailed panel keeps all of them too -
    /// both surfaces call the same view model and refresh from the same list, so there is one
    /// source of truth and never two numbers that disagree.
    /// </para>
    /// Whole groups wrap to the available editor width. Empty selection gives the space back
    /// to the canvas; viewport resizing preserves the visible world centre.
    /// </summary>
    private Control BuildQuickPropertyBar()
    {
        var thumbnail = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        }.Named("QuickThumbnail");

        var identity = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            FontWeight = FontWeight.Medium,
        }.Named("QuickIdentity");

        var layer = BuildOwnerLayerPicker("Quick");
        layer.Width = 152;
        layer.HorizontalAlignment = HorizontalAlignment.Left;

        var (levelBlock, _) = BuildQuickLevel();

        var scale = BuildQuickNumber(
            "QuickScale", "Масштаб", "%", 10, 400,
            () => _editor.SelectionScalePercent,
            value => _editor.SetSelectionScalePercent(value, commit: true));
        var opacity = BuildQuickNumber(
            "QuickOpacity", "Непрозрачность", "%", 0, 100,
            () => _editor.SelectionOpacityPercent,
            value => _editor.SetSelectionOpacityPercent(value, commit: true));
        var rotation = BuildQuickNumber(
            "QuickRotation", "Поворот", "°", 0, 359,
            () => _editor.SelectionRotationDegrees,
            value => _editor.SetSelectionRotationDegrees(value, commit: true));

        // The size in map units, for the one object whose size has one meaning. It writes
        // through the same geometry commit the panel's fields use: one command, one history entry.
        var width = BuildQuickNumber(
            "QuickWidth", "Ширина", "px", 1, 1_000_000,
            () => _editor.SelectionCount == 1 ? _editor.Session.SelectedObject?.Width : null,
            value => CommitQuickSize(value, isWidth: true));
        var height = BuildQuickNumber(
            "QuickHeight", "Высота", "px", 1, 1_000_000,
            () => _editor.SelectionCount == 1 ? _editor.Session.SelectedObject?.Height : null,
            value => CommitQuickSize(value, isWidth: false));
        var size = QuickGroup(width, height);

        // Order inside the level: the four moves a selected object needs most often, as
        // arrows next to the level they act within, with the current shortcut in the tip.
        Button Order(string name, string caption, string icon, int direction, bool extreme, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderSelection(direction, extreme);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var ordering = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Order("QuickRaiseTopButton", "Наверх: сделать выделение самым верхним в его слое",
                    MapIcons.ArrowToTop, 1, true, EditorShortcutSettings.Commands.ObjectTop),
                Order("QuickRaiseButton", "Выше: поднять выделение над соседями того же уровня",
                    MapIcons.ArrowUp, 1, false, EditorShortcutSettings.Commands.ObjectUp),
                Order("QuickLowerButton", "Ниже: опустить выделение под соседей того же уровня",
                    MapIcons.ArrowDown, -1, false, EditorShortcutSettings.Commands.ObjectDown),
                Order("QuickLowerBottomButton", "Вниз: сделать выделение самым нижним в его слое",
                    MapIcons.ArrowToBottom, -1, true, EditorShortcutSettings.Commands.ObjectBottom),
            },
        });
        var stacking = QuickGroup(QuickField("Слой", layer), levelBlock, ordering);
        var appearance = QuickGroup(scale, opacity, rotation);

        // The same strip for a piece of land: the Land tool works on one land layer at a time,
        // and what the owner reaches for there is where that piece sits among the others and
        // how much it lets through. Land is always below objects, so its order is among land.
        Button LandOrder(string name, string caption, string icon, bool up, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderActiveLayer(up);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var landOrder = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                LandOrder("QuickLandUpButton", "Выше: поднять этот слой земли над соседним",
                    MapIcons.ArrowUp, true, EditorShortcutSettings.Commands.LayerUp),
                LandOrder("QuickLandDownButton", "Ниже: опустить этот слой земли под соседний",
                    MapIcons.ArrowDown, false, EditorShortcutSettings.Commands.LayerDown),
            },
        });
        var landOpacity = BuildQuickNumber(
            "QuickLandOpacity", "Непрозрачность", "%", 0, 100,
            () => _editor.ActiveLayer is LandLayer active ? active.Opacity * 100 : null,
            value => _editor.SetActiveLayerOpacity(value),
            () => _editor.ActiveLayer is LandLayer);
        var landVisible = Ui.TextButton("QuickLandVisibilityButton", "Показан", () =>
        {
            _editor.ToggleActiveLayerVisibility();
            RefreshAfterSelectionCommand();
        }, "compact");
        var landLevel = BuildQuickLevelBlock("QuickLandLevel", "Уровень земли",
            () => _editor.LandTargetLevel,
            value => _editor.SetLandLevel(value),
            delta => _editor.ShiftLandLevel(delta),
            () => _editor.LandTarget is not null);
        var land = QuickGroup(landLevel, landOrder, landOpacity, QuickField("Видимость", landVisible));

        // Water and roads have the same level and order as everything else on the map.
        var waterLevel = BuildQuickLevelBlock("QuickWaterLevel", "Уровень воды или дороги",
            () => _editor.SurfaceSelectionLevelValue,
            value => _editor.SetSelectedSurfaceLevel(value),
            delta => _editor.ShiftSelectedSurfaceLevel(delta),
            () => _editor.SurfaceSelectionCount > 0);
        Button WaterOrder(string name, string caption, string icon, int direction, bool extreme, string command)
        {
            var button = Ui.IconButton(name, caption, icon, () =>
            {
                _editor.ReorderSelection(direction, extreme);
                RefreshAfterSelectionCommand();
            });
            DynamicTip(button, () => caption + " (" + _editor.Shortcuts.Get(command) + ")");
            return button;
        }
        var waterOrder = QuickField("Порядок", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                WaterOrder("QuickWaterTopButton", "Наверх: сделать воду самой верхней среди воды слоя",
                    MapIcons.ArrowToTop, 1, true, EditorShortcutSettings.Commands.ObjectTop),
                WaterOrder("QuickWaterUpButton", "Выше: поднять воду над соседней водой",
                    MapIcons.ArrowUp, 1, false, EditorShortcutSettings.Commands.ObjectUp),
                WaterOrder("QuickWaterDownButton", "Ниже: опустить воду под соседнюю воду",
                    MapIcons.ArrowDown, -1, false, EditorShortcutSettings.Commands.ObjectDown),
                WaterOrder("QuickWaterBottomButton", "Вниз: сделать воду самой нижней среди воды слоя",
                    MapIcons.ArrowToBottom, -1, true, EditorShortcutSettings.Commands.ObjectBottom),
            },
        });
        var waterGroup = QuickGroup(waterLevel, waterOrder);

        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12, LineSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                QuickGroup(thumbnail, identity),
                stacking,
                waterGroup,
                size,
                appearance,
                land,
            },
        };
        var host = new Border { Padding = new Thickness(10, 6), Child = strip }
            .Classed("quick-bar").Named("QuickPropertyBar");

        _quickSyncs.Add(() =>
        {
            var count = _editor.SelectionCount;
            var water = _editor.SurfaceSelectionCount;
            var selected = _editor.Session.SelectedObject;
            // A selected piece of land, or the active land layer under the Land tool.
            var landLayer = _editor.SelectedLand
                ?? (count == 0 && water == 0 && _editor.ActiveTool == EditorTool.Land ? _editor.ActiveLayer as LandLayer : null);
            // Water is a selection too: the strip names it and offers its layer, and keeps the
            // object-only fields out of the way. Under the Land tool the strip speaks for the
            // active piece of land.
            host.IsVisible = count > 0 || water > 0 || landLayer is not null;

            thumbnail.Source = count == 1 && selected is not null
                ? _thumbnails.Image(_editor.ObjectThumbnailPath(selected))
                : null;
            thumbnail.IsVisible = thumbnail.Source is not null;
            identity.Text = count switch
            {
                0 when water > 0 => "Выделено: " + _editor.SelectionStatus.TrimEnd('.'),
                0 when landLayer is not null => "Земля: " + landLayer.Name,
                0 => "",
                1 => "Выделено: " + (selected?.Name ?? "Объект"),
                _ => "Выбрано: " + EditorLabels.Objects(count),
            };
            ToolTip.SetTip(identity, identity.Text);
            AutomationProperties.SetName(identity, identity.Text);

            stacking.IsVisible = landLayer is null;
            levelBlock.IsVisible = count > 0;
            ordering.IsVisible = count > 0;
            waterGroup.IsVisible = count == 0 && water > 0;
            appearance.IsVisible = count > 0;
            size.IsVisible = count == 1;
            land.IsVisible = landLayer is not null;
            if (landLayer is not null)
            {
                landVisible.Content = landLayer.Visible ? "Показан" : "Скрыт";
                AutomationProperties.SetName(landVisible, "Видимость слоя земли: " + landVisible.Content);
            }

            // A percentage of a bitmap's own reference size means nothing for a shape or a
            // caption, so the field is absent rather than lying with a blank.
            scale.IsVisible = _editor.SelectionIsAllStamps;
        });

        return host;
    }

    /// <summary>
    /// One side of the size from the strip. The other fields are refreshed from the object first,
    /// so only the typed side changes; a locked stamp aspect follows it through the same commit.
    /// </summary>
    private void CommitQuickSize(double value, bool isWidth)
    {
        _editor.RefreshInspector();
        var text = value.ToString("0", CultureInfo.InvariantCulture);
        if (isWidth) _editor.WidthText = text; else _editor.HeightText = text;
        _editor.CommitInspector();
        SyncInspector();
    }

    private static Control QuickGroup(params Control[] fields)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.AddRange(fields);
        return new Border { Child = row, Padding = new Thickness(8, 2) }.Classed("property-group");
    }

    /// <summary>A caption and its editor side by side, the strip's single row shape.</summary>
    private static Control QuickField(string caption, Control editor)
    {
        var label = new TextBlock
        {
            Text = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        }.Classed("muted");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, editor },
        };
    }

    /// <summary>
    /// The level in the strip: the exact number, typed or stepped, plus a reset. It writes through
    /// the same view model the slider in the panel writes through, so the two always agree.
    /// </summary>
    private (Control Block, TextBox Box) BuildQuickLevel()
    {
        var box = new TextBox
        {
            Width = 62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named("QuickLevelBox");
        AutomationProperties.SetName(box, "Уровень объекта");

        var syncing = false;

        void Commit()
        {
            if (syncing)
            {
                return;
            }

            var text = (box.Text ?? "").Trim().Replace('−', '-').Replace('+', ' ').Trim();
            if (text.Length == 0)
            {
                SyncQuickBar();
                return;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                _editor.ReportInspectorError(
                    "Уровень — целое число от " + EditorViewModel.MinimumSublayer
                        + " до +" + EditorViewModel.MaximumSublayer + ".");
                SyncQuickBar();
                return;
            }

            var bounded = Math.Clamp(
                value, EditorViewModel.MinimumSublayer, EditorViewModel.MaximumSublayer);
            if (bounded != value)
            {
                _editor.ReportInspectorError(
                    "Уровень ограничен диапазоном " + EditorViewModel.MinimumSublayer
                        + "…+" + EditorViewModel.MaximumSublayer + ".");
            }

            _editor.SetSelectionSublayer(bounded, commit: true);
            RefreshAfterSelectionCommand();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SyncQuickBar();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        Button Step(string name, string caption, int direction, string tip)
        {
            var button = Ui.TextButton(name, caption, () =>
            {
                _editor.ShiftSelectionSublayer(direction);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }

        var reset = ResetGlyph("QuickLevelResetButton", "Сбросить уровень к нулю", () =>
        {
            _editor.SetSelectionSublayer(0, commit: true);
            RefreshAfterSelectionCommand();
        });

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                box.Text = _editor.SelectionSublayerReadout;
                Ui.SetClass(box, "mixed", _editor.SelectionSublayerIsMixed);
                box.IsEnabled = _editor.SelectionCount > 0;
                reset.IsEnabled = _editor.SelectionCount > 0;
                AutomationProperties.SetName(box, "Уровень объекта: " + box.Text);
                ToolTip.SetTip(box,
                    "Уровень выделения, целое число от " + EditorViewModel.MinimumSublayer
                        + " до +" + EditorViewModel.MaximumSublayer
                        + ". Введите значение и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        var block = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "Уровень",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                }.Classed("muted"),
                Step("QuickLevelDownButton", "−", -1, "Опустить выделение на один уровень."),
                box,
                Step("QuickLevelUpButton", "+", 1, "Поднять выделение на один уровень."),
                reset,
            },
        };
        return (block, box);
    }

    /// <summary>
    /// A level entry for water or land: the same -10..+10 band, the same −/+ steps, the same
    /// reset, writing through the given commands. Objects keep their own block; this one serves
    /// everything else on the map so the strip reads the same whatever is selected.
    /// </summary>
    private Control BuildQuickLevelBlock(
        string name, string what, Func<int?> read, Action<int> set, Action<int> shift, Func<bool> enabled)
    {
        var box = new TextBox
        {
            Width = 62,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named(name + "Box");
        AutomationProperties.SetName(box, what);
        var syncing = false;

        void Commit()
        {
            if (syncing) return;
            var text = (box.Text ?? "").Trim().Replace('−', '-').Replace('+', ' ').Trim();
            if (text.Length == 0 || text == "разные") { SyncQuickBar(); return; }
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                _editor.ReportInspectorError(what + " — целое число от " + TerrainOrder.MinimumLevel + " до +" + TerrainOrder.MaximumLevel + ".");
                SyncQuickBar();
                return;
            }
            set(TerrainOrder.Clamp(value));
            RefreshAfterSelectionCommand();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SyncQuickBar(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();

        Button Step(string suffix, string caption, int direction, string tip)
        {
            var button = Ui.TextButton(name + suffix, caption, () =>
            {
                shift(direction);
                RefreshAfterSelectionCommand();
            }, "compact");
            ToolTip.SetTip(button, tip);
            return button;
        }
        var reset = ResetGlyph(name + "ResetButton", "Сбросить уровень к нулю", () =>
        {
            set(0);
            RefreshAfterSelectionCommand();
        });

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                var on = enabled();
                var value = read();
                box.Text = !on ? "—" : value is { } level ? EditorViewModel.FormatSublayer(level) : "разные";
                Ui.SetClass(box, "mixed", on && value is null);
                box.IsEnabled = on;
                reset.IsEnabled = on;
                AutomationProperties.SetName(box, what + ": " + box.Text);
                ToolTip.SetTip(box, what + ", целое число от " + TerrainOrder.MinimumLevel + " до +" + TerrainOrder.MaximumLevel
                    + ". Земля и вода более высокого уровня накрывают более низкие. Введите значение и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Уровень", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }.Classed("muted"),
                Step("DownButton", "−", -1, "Опустить на один уровень."),
                box,
                Step("UpButton", "+", 1, "Поднять на один уровень."),
                reset,
            },
        };
    }

    /// <summary>
    /// One compact numeric field of the strip. Empty means "the selected objects disagree", which
    /// is the honest answer; typing a number gives all of them that value as one undoable command.
    /// </summary>
    private Control BuildQuickNumber(
        string name, string caption, string unit, double minimum, double maximum,
        Func<double?> read, Action<double> apply, Func<bool>? enabled = null)
    {
        enabled ??= () => _editor.SelectionCount > 0;
        var box = new TextBox
        {
            Width = 68,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        }.Named(name + "Box");

        var syncing = false;

        void Commit()
        {
            if (syncing)
            {
                return;
            }

            var text = (box.Text ?? "").Trim().Replace(',', '.').Replace('−', '-');
            if (text.Length == 0 || string.Equals(text, "разные", StringComparison.Ordinal))
            {
                SyncQuickBar();
                return;
            }

            if (!TryWholeNumber(text, out var value))
            {
                _editor.ReportInspectorError($"{caption} — целое число от {minimum:0} до {maximum:0} {unit}.");
                SyncQuickBar();
                return;
            }

            var bounded = Math.Clamp(value, minimum, maximum);
            if (Math.Abs(bounded - value) > 0.0001)
            {
                _editor.ReportInspectorError($"{caption} ограничен диапазоном {minimum:0}…{maximum:0} {unit}.");
            }

            apply(bounded);
            RefreshAfterSelectionCommand();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SyncQuickBar();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        _quickSyncs.Add(() =>
        {
            syncing = true;
            try
            {
                var value = read();
                box.Text = value is { } number
                    ? number.ToString("0", CultureInfo.InvariantCulture)
                    : _editor.SelectionCount > 1 ? "разные" : "";
                Ui.SetClass(box, "mixed", value is null && _editor.SelectionCount > 1);
                box.IsEnabled = enabled();
                AutomationProperties.SetName(box, $"{caption}, {unit}: {box.Text}");
                ToolTip.SetTip(box,
                    $"{caption} выделения, {minimum:0}…{maximum:0} {unit}, только целые значения. Введите число и нажмите Enter.");
            }
            finally
            {
                syncing = false;
            }
        });

        return QuickField(caption, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                box,
                new TextBlock
                {
                    Text = unit,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Classed("muted"),
            },
        });
    }

    /// <summary>Refreshes the strip and the object panel together - they share one list.</summary>
    private void SyncQuickBar()
    {
        foreach (var sync in _quickSyncs)
        {
            sync();
        }
    }
}
