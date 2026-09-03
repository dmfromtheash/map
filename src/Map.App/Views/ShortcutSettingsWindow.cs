using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.App.Services;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>A transactional editor: Cancel leaves live bindings untouched; Save applies all.</summary>
public sealed class ShortcutSettingsWindow : Window
{
    private readonly EditorShortcutSettings _draft;
    private readonly Func<EditorShortcutSettings, string?> _saveDraft;
    private readonly TextBox _search = new()
    {
        Name = "ShortcutSearchBox",
        PlaceholderText = "Найти команду, группу или контекст",
    };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _selectedName = new() { Name = "SelectedShortcutCommandText" };
    private readonly TextBlock _selectedContext = new() { Name = "SelectedShortcutContextText" };
    private readonly TextBox _capture = new()
    {
        Name = "ShortcutGestureInput",
        IsReadOnly = true,
        PlaceholderText = "Нажмите новое сочетание",
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _conflict = new()
    {
        Name = "ShortcutConflictText",
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _warning = new()
    {
        Name = "ShortcutSettingsWarningText",
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Button _save = new() { Name = "SaveShortcutSettingsButton", Content = "Сохранить" };
    private ShortcutCommandDefinition? _selected;

    public ShortcutSettingsWindow(
        EditorShortcutSettings settings,
        Func<EditorShortcutSettings, string?> saveDraft,
        string? loadWarning)
    {
        _draft = new EditorShortcutSettings(settings.Bindings);
        _saveDraft = saveDraft;

        Title = "Сочетания клавиш";
        Name = "ShortcutSettingsWindow";
        Width = 1040;
        Height = 760;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(_search, "Поиск команд");
        AutomationProperties.SetName(_capture, "Новое сочетание");
        _conflict.Classes.Add("danger");
        _warning.Classes.Add("warn");
        _save.Classes.Add("primary");
        _warning.Text = loadWarning ?? "";
        _warning.IsVisible = !string.IsNullOrWhiteSpace(loadWarning);

        var unassign = Button("UnassignShortcutButton", "Не назначено", () =>
        {
            if (_selected is null) return;
            _draft.Set(_selected.Id, "");
            RefreshAll();
        });
        var resetOne = Button("ResetShortcutButton", "Стандартное", () =>
        {
            if (_selected is null) return;
            _draft.Reset(_selected.Id);
            RefreshAll();
        });
        var resetAll = Button("ResetAllShortcutsButton", "Сбросить все", () =>
        {
            _draft.ResetAll();
            RefreshAll();
        });
        var cancel = Button("CancelShortcutSettingsButton", "Отмена", Close);
        _save.Click += (_, _) => Save();
        AutomationProperties.SetName(_save, "Сохранить сочетания");

        _capture.KeyDown += CaptureGesture;
        _search.TextChanged += (_, _) => RebuildRows();

        var shortcutSurface = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("3*,2*"),
            Children =
            {
                new Border
                {
                    Padding = new Thickness(22, 14),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 10,
                                Children =
                                {
                                    MapIcons.Stroked(
                                        MapIcons.Select, 21,
                                        Application.Current?.FindResource("MapAccentBrush") as Avalonia.Media.IBrush),
                                    new TextBlock
                                    {
                                        Text = "Сочетания клавиш",
                                        FontFamily = "Georgia",
                                        FontSize = 27,
                                    },
                                },
                            },
                            new TextBlock
                            {
                                Text = "Выберите команду, затем нажмите новое сочетание. Изменения вступят в силу только после сохранения.",
                                TextWrapping = TextWrapping.Wrap,
                            }.Classed("muted"),
                            _warning,
                        },
                    },
                }.Classed("app-bar").GridColumnSpan(2),
                new Border
                {
                    Margin = new Thickness(18, 0, 8, 12),
                    Padding = new Thickness(12),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            _search.Docked(Dock.Top),
                            new ScrollViewer { Margin = new Thickness(0, 10, 0, 0), Content = _rows },
                        },
                    },
                }.Classed("elevated").GridRow(1),
                new Border
                {
                    Margin = new Thickness(8, 0, 18, 12),
                    Padding = new Thickness(16),
                    Child = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            _selectedName,
                            _selectedContext,
                            new TextBlock { Text = "Новое сочетание" },
                            _capture,
                            _conflict,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { unassign, resetOne },
                            },
                        },
                    },
                }.Classed("elevated").GridRow(1).GridColumn(1),
                new Border
                {
                    Padding = new Thickness(18, 10),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            resetAll.Docked(Dock.Left),
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Children = { cancel, _save },
                            }.Docked(Dock.Right),
                        },
                    },
                }.Classed("app-bar").GridRow(2).GridColumnSpan(2),
            },
        };

        Content = Ui.WatercolorBackdrop(new DockPanel
        {
            Children =
            {
                DecorativeRail().Docked(Dock.Left),
                shortcutSurface,
            },
        });

        _selected = EditorShortcutSettings.Definitions.First(definition => definition.IsEditable);
        RefreshAll();
        Opened += (_, _) => _search.Focus();
    }

    private void CaptureGesture(object? sender, KeyEventArgs args)
    {
        // Bare Tab and Shift+Tab remain standard focus navigation. Modified Tab combinations can
        // still be captured, while the dialog itself never traps a keyboard-only user here.
        if (args.Key == Key.Tab
            && !args.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        if (_selected is null || !ShortcutKeyAdapter.TryCreate(args, out var input))
        {
            return;
        }

        _draft.Set(_selected.Id, new ShortcutGesture(input.Key, input.Modifiers).ToString());
        args.Handled = true;
        RefreshAll();
    }

    private void RebuildRows()
    {
        var query = _search.Text?.Trim() ?? "";
        _rows.Children.Clear();
        foreach (var group in EditorShortcutSettings.Definitions
                     .Where(definition => definition.IsEditable && Matches(definition, query))
                     .GroupBy(definition => definition.Group))
        {
            _rows.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 10, 2, 2),
            });
            foreach (var definition in group)
            {
                var gesture = _draft.Get(definition.Id);
                var row = Button(
                    "ShortcutCommand_" + definition.Id.Replace('-', '_'),
                    $"{definition.Name}   ·   {(gesture.Length == 0 ? "Не назначено" : gesture)}",
                    () =>
                    {
                        _selected = definition;
                        RefreshSelection();
                        _capture.Focus();
                    });
                row.HorizontalContentAlignment = HorizontalAlignment.Left;
                AutomationProperties.SetName(row,
                    $"{definition.Name}. {EditorShortcutSettings.ContextName(definition.Context)}. " +
                    (gesture.Length == 0 ? "Не назначено" : gesture));
                _rows.Children.Add(row);
            }
        }
    }

    private static bool Matches(ShortcutCommandDefinition definition, string query) =>
        query.Length == 0
        || definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || definition.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
        || definition.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || EditorShortcutSettings.ContextName(definition.Context)
            .Contains(query, StringComparison.OrdinalIgnoreCase);

    private static Control DecorativeRail()
    {
        var icons = new[]
        {
            MapIcons.Select, MapIcons.Line, MapIcons.Assets, MapIcons.Shape,
            MapIcons.Text, MapIcons.Layers, MapIcons.Notes, MapIcons.Undo,
            MapIcons.Redo, MapIcons.Duplicate, MapIcons.Import, MapIcons.Objects,
            MapIcons.FitCanvas, MapIcons.Rotate,
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(9, 16),
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var icon in icons)
        {
            panel.Children.Add(MapIcons.Stroked(icon, 19));
        }

        return new Border
        {
            Width = 76,
            Child = panel,
        }.Classed("rail");
    }

    private void RefreshAll()
    {
        RebuildRows();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_selected is null)
        {
            return;
        }

        _selectedName.Text = _selected.Name;
        _selectedContext.Text = $"Контекст: {EditorShortcutSettings.ContextName(_selected.Context)}\n{_selected.Description}";
        _capture.Text = _draft.Get(_selected.Id) is { Length: > 0 } gesture ? gesture : "Не назначено";

        var conflicts = _draft.GetConflicts();
        _conflict.Text = conflicts.Count == 0
            ? ""
            : string.Join(Environment.NewLine, conflicts.Select(conflict =>
                $"Конфликт {conflict.Gesture}: «{conflict.First.Name}» и «{conflict.Second.Name}»."));
        _conflict.IsVisible = conflicts.Count > 0;
        _save.IsEnabled = conflicts.Count == 0;
    }

    private void Save()
    {
        if (_draft.GetConflicts().Count != 0)
        {
            RefreshSelection();
            return;
        }

        var error = _saveDraft(_draft);
        if (error is null)
        {
            Close();
            return;
        }

        _warning.Text = error;
        _warning.IsVisible = true;
    }

    private static Button Button(string name, string text, Action action)
    {
        var button = new Button { Name = name, Content = text };
        button.Click += (_, _) => action();
        return button;
    }
}

internal static class ShortcutGridPlacement
{
    public static T GridRow<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    public static T GridColumn<T>(this T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    public static T GridColumnSpan<T>(this T control, int span) where T : Control
    {
        Grid.SetColumnSpan(control, span);
        return control;
    }
}
