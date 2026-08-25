using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The editor screen, built in code so every control carries an explicit UIA name. The view only
/// reads the view-model and forwards user intent; it never owns document state.
/// </summary>
public sealed class EditorView : UserControl
{
    private readonly EditorViewModel _editor;
    private readonly MapCanvasControl _canvas = new();
    private readonly TextBlock _saveState = Status("SaveStateText");
    private readonly TextBlock _statusDetail = Status("StatusDetailText");
    private readonly TextBlock _viewportStatus = Status("ViewportStatusText");
    private readonly TextBlock _selectionStatus = Status("SelectionStatusText");
    private readonly TextBlock _inspectorError = Status("InspectorErrorText");
    private readonly TextBox _xBox = NumberBox("XBox");
    private readonly TextBox _yBox = NumberBox("YBox");
    private readonly TextBox _widthBox = NumberBox("WidthBox");
    private readonly TextBox _heightBox = NumberBox("HeightBox");
    private readonly TextBox _rotationBox = NumberBox("RotationBox");

    public EditorView(EditorViewModel editor, Func<System.Threading.Tasks.Task> requestBack)
    {
        _editor = editor;
        Name = "EditorView";

        Button Tool(string name, string text, Action action)
        {
            var button = new Button { Name = name, Content = text };
            button.Click += (_, _) => action();
            return button;
        }

        Button AsyncTool(string name, string text, Func<System.Threading.Tasks.Task> action)
        {
            var button = new Button { Name = name, Content = text };
            button.Click += async (_, _) => await action();
            return button;
        }

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 8),
            Children =
            {
                AsyncTool("BackButton", "← Библиотека", requestBack),
                new Separator { Width = 12 },
                Tool("AddRectangleButton", "Прямоугольник", () => _editor.AddObject(SceneObjectKind.Rectangle)),
                Tool("AddEllipseButton", "Эллипс", () => _editor.AddObject(SceneObjectKind.Ellipse)),
                new Separator { Width = 12 },
                Tool("UndoButton", "Отменить", _editor.Undo),
                Tool("RedoButton", "Повторить", _editor.Redo),
                new Separator { Width = 12 },
                AsyncTool("SaveButton", "Сохранить", () => _editor.SaveAsync()),
                AsyncTool("SaveAsButton", "Сохранить как…", () => _editor.SaveAsync(saveAs: true)),
                Tool("FitButton", "Вписать", _canvas.FitToCanvas),
            },
        };

        var inspector = BuildInspector();

        var statusBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Margin = new Thickness(10, 6),
            Children = { _saveState, _selectionStatus, _viewportStatus, _statusDetail },
        };

        Content = new DockPanel
        {
            Children =
            {
                WithDock(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x27)),
                    Child = toolbar,
                }, Dock.Top),
                WithDock(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x27)),
                    Child = statusBar,
                }, Dock.Bottom),
                WithDock(new Border
                {
                    Width = 260,
                    Background = new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x30)),
                    Child = inspector,
                }, Dock.Right),
                _canvas,
            },
        };

        _canvas.Attach(_editor);
        _canvas.ViewportChanged += (_, _) => _viewportStatus.Text = _editor.ViewportStatus;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.SceneChanged += (_, _) => Dispatcher.UIThread.Post(SyncStatus);

        SyncStatus();
        SyncInspector();

        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(_canvas.FitToCanvas, DispatcherPriority.Loaded);
    }

    public EditorViewModel Editor => _editor;

    private Control BuildInspector()
    {
        Control Row(string label, TextBox box)
        {
            return new DockPanel
            {
                Margin = new Thickness(0, 2),
                Children =
                {
                    WithDock(new TextBlock
                    {
                        Text = label,
                        Width = 84,
                        VerticalAlignment = VerticalAlignment.Center,
                    }, Dock.Left),
                    box,
                },
            };
        }

        var apply = new Button { Name = "ApplyButton", Content = "Применить", Margin = new Thickness(0, 8, 0, 0) };
        apply.Click += (_, _) => CommitInspector();

        foreach (var box in new[] { _xBox, _yBox, _widthBox, _heightBox, _rotationBox })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    CommitInspector();
                    e.Handled = true;
                }
            };
        }

        return new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Свойства объекта", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                Row("X", _xBox),
                Row("Y", _yBox),
                Row("Ширина", _widthBox),
                Row("Высота", _heightBox),
                Row("Поворот °", _rotationBox),
                apply,
                _inspectorError,
            },
        };
    }

    private void CommitInspector()
    {
        _editor.XText = _xBox.Text ?? "";
        _editor.YText = _yBox.Text ?? "";
        _editor.WidthText = _widthBox.Text ?? "";
        _editor.HeightText = _heightBox.Text ?? "";
        _editor.RotationText = _rotationBox.Text ?? "";
        _editor.CommitInspector();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SyncStatus();
            if (e.PropertyName is nameof(EditorViewModel.XText) or nameof(EditorViewModel.YText)
                or nameof(EditorViewModel.WidthText) or nameof(EditorViewModel.HeightText)
                or nameof(EditorViewModel.RotationText) or nameof(EditorViewModel.HasEditableSelection))
            {
                SyncInspector();
            }
        });

    private void SyncStatus()
    {
        _saveState.Text = _editor.SaveStateText;
        _statusDetail.Text = _editor.StatusDetail;
        _selectionStatus.Text = _editor.SelectionStatus;
        _viewportStatus.Text = _editor.ViewportStatus;
        _inspectorError.Text = _editor.InspectorError;
    }

    private void SyncInspector()
    {
        _xBox.Text = _editor.XText;
        _yBox.Text = _editor.YText;
        _widthBox.Text = _editor.WidthText;
        _heightBox.Text = _editor.HeightText;
        _rotationBox.Text = _editor.RotationText;
        var editable = _editor.HasEditableSelection;
        _xBox.IsEnabled = editable;
        _yBox.IsEnabled = editable;
        _widthBox.IsEnabled = editable;
        _heightBox.IsEnabled = editable;
        _rotationBox.IsEnabled = editable;
    }

    public MapCanvasControl Canvas => _canvas;

    private static TextBlock Status(string name) => new()
    {
        Name = name,
        Text = "",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox NumberBox(string name) => new()
    {
        Name = name,
        MinWidth = 110,
    };

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
