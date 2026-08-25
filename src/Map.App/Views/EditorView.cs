using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Map.App.Controls;
using Map.Assets;
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
    private readonly StackPanel _layerList = new() { Spacing = 4 };
    private readonly StackPanel _objectList = new() { Spacing = 4 };
    private readonly TextBox _layerName = new() { Name = "LayerNameBox", PlaceholderText = "Имя слоя" };
    private readonly TextBox _objectSearch = new() { Name = "ObjectSearchBox", PlaceholderText = "Поиск объектов" };
    private readonly StackPanel _assetList = new() { Spacing = 4 };
    private readonly TextBox _assetSearch = new() { Name = "AssetSearchBox", PlaceholderText = "Поиск ассетов" };
    private readonly StackPanel _noteList = new() { Spacing = 4 };
    private readonly TextBox _noteSearch = new() { Name = "NoteSearchBox", PlaceholderText = "Поиск заметок" };
    private readonly TextBox _noteTitle = new() { Name = "NoteTitleBox", PlaceholderText = "Заголовок" };
    private readonly TextBox _noteMarkdown = new()
    {
        Name = "NoteMarkdownBox", PlaceholderText = "Markdown без HTML", AcceptsReturn = true, MinHeight = 90,
    };
    private NoteId? _selectedNoteId;
    private AssetRecord? _selectedAsset;
    private readonly WrapPanel _commonObjectActions = new() { Name = "CommonObjectActions" };
    private readonly WrapPanel _stampObjectActions = new() { Name = "StampObjectActions" };
    private readonly WrapPanel _vectorObjectActions = new() { Name = "VectorObjectActions" };
    private readonly WrapPanel _textObjectActions = new() { Name = "TextObjectActions" };
    private readonly WrapPanel _compositionObjectActions = new() { Name = "CompositionObjectActions" };
    private Control _stampObjectGroup = null!;
    private Control _vectorObjectGroup = null!;
    private Control _textObjectGroup = null!;
    private Control _compositionObjectGroup = null!;

    public EditorView(
        EditorViewModel editor, Func<System.Threading.Tasks.Task> requestBack, Action requestSecondWindow)
    {
        _editor = editor;
        Name = "EditorView";

        Button Tool(string name, string text, Action action)
        {
            var button = new Button { Name = name, Content = text, Margin = new Thickness(3) };
            ToolTip.SetTip(button, text);
            button.Click += (_, _) => action();
            return button;
        }

        Button AsyncTool(string name, string text, Func<System.Threading.Tasks.Task> action)
        {
            var button = new Button { Name = name, Content = text, Margin = new Thickness(3) };
            button.Click += async (_, _) => await action();
            return button;
        }

        var brushSize = new Slider
        {
            Name = "BrushSizeSlider",
            Minimum = 1,
            Maximum = 256,
            Value = 24,
            Width = 110,
            Margin = new Thickness(3),
        };
        brushSize.ValueChanged += (_, _) =>
            _editor.BrushSettings = _editor.BrushSettings with { Radius = brushSize.Value };

        var brushOpacity = new Slider
        {
            Name = "BrushOpacitySlider",
            Minimum = 0.05,
            Maximum = 1,
            Value = 1,
            Width = 80,
            Margin = new Thickness(3),
        };
        brushOpacity.ValueChanged += (_, _) =>
            _editor.BrushSettings = _editor.BrushSettings with { Opacity = brushOpacity.Value };

        var eraser = Tool("EraserButton", "Ластик: выкл", () => { });
        eraser.Click += (_, _) =>
        {
            _editor.BrushSettings = _editor.BrushSettings with { Eraser = !_editor.BrushSettings.Eraser };
            eraser.Content = _editor.BrushSettings.Eraser ? "Ластик: вкл" : "Ластик: выкл";
        };

        var primaryToolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(7, 5),
            Children =
            {
                AsyncTool("BackButton", "← Библиотека", requestBack),
                Tool("SecondWindowButton", "Второе окно", requestSecondWindow),
                new Separator { Width = 12 },
                Tool("SelectToolButton", "Выбор", () => _editor.ActiveTool = EditorTool.Select),
                Tool("UndoButton", "Отменить", _editor.Undo),
                Tool("RedoButton", "Повторить", _editor.Redo),
                new Separator { Width = 12 },
                AsyncTool("SaveButton", "Сохранить", () => _editor.SaveAsync()),
                AsyncTool("SaveAsButton", "Сохранить как…", () => _editor.SaveAsync(saveAs: true)),
                Tool("FitButton", "Вписать", _canvas.FitToCanvas),
            },
        };
        var objectTools = new WrapPanel
        {
            Children =
            {
                Tool("LineToolButton", "Линия", () => _editor.ActiveTool = EditorTool.Line),
                Tool("ShapeToolButton", "Фигура", () => _editor.ActiveTool = EditorTool.Shape),
                Tool("TextToolButton", "Текст", () => _editor.ActiveTool = EditorTool.Text),
                Tool("AddRectangleButton", "Прямоугольник", () => _editor.AddObject(SceneObjectKind.Rectangle)),
                Tool("AddEllipseButton", "Эллипс", () => _editor.AddObject(SceneObjectKind.Ellipse)),
                Tool("AddLineButton", "+ Линия", _editor.AddLine),
                Tool("AddVectorRectangleButton", "+ Фигура", () => _editor.AddShape(ShapePrimitiveKind.Rectangle)),
                Tool("AddTextButton", "+ Текст", _editor.AddText),
            },
        };
        var rasterTools = new WrapPanel
        {
            Children =
            {
                Tool("BrushToolButton", "Кисть", () => _editor.ActiveTool = EditorTool.Brush),
                Tool("MaskToolButton", "Маска", () => _editor.ActiveTool = EditorTool.Mask),
                Tool("LandToolButton", "Земля", () => _editor.ActiveTool = EditorTool.Land),
                brushSize, brushOpacity, eraser,
                Tool("MaskModeButton", "Маска +/−", () =>
                    _editor.MaskMode = _editor.MaskMode == Map.Core.Editing.MaskPaintMode.Hide
                        ? Map.Core.Editing.MaskPaintMode.Reveal
                        : Map.Core.Editing.MaskPaintMode.Hide),
            },
        };
        var toolbar = new StackPanel
        {
            Children =
            {
                primaryToolbar,
                new TabControl
                {
                    Name = "ToolPaletteTabs", Margin = new Thickness(7, 0, 7, 5),
                    ItemsSource = new[]
                    {
                        new TabItem { Header = "Объекты", Content = objectTools },
                        new TabItem { Header = "Кисти и маски", Content = rasterTools },
                    },
                },
            },
        };

        var rightPanel = BuildRightPanel();

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
                    Width = 310,
                    Background = new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x30)),
                    Child = rightPanel,
                }, Dock.Right),
                _canvas,
            },
        };

        _canvas.Attach(_editor);
        _canvas.ViewportChanged += (_, _) => _viewportStatus.Text = _editor.ViewportStatus;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.SceneChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SyncStatus();
            SyncSidePanels();
        });
        _objectSearch.TextChanged += (_, _) => SyncObjectList();
        _assetSearch.TextChanged += (_, _) => SyncAssetList();
        _noteSearch.TextChanged += (_, _) => SyncNoteList();

        SyncStatus();
        SyncInspector();
        SyncSidePanels();

        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(_canvas.FitToCanvas, DispatcherPriority.Loaded);
    }

    public EditorViewModel Editor => _editor;

    private Control BuildRightPanel()
    {
        Button ActionButton(string name, string text, Action action)
        {
            var button = new Button { Name = name, Content = text, Margin = new Thickness(2) };
            ToolTip.SetTip(button, text);
            button.Click += (_, _) => action();
            return button;
        }

        static Control ContextGroup(string title, Control content) => new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Opacity = 0.8 },
                content,
            },
        };

        var rename = ActionButton("RenameLayerButton", "Переименовать", () =>
            _editor.RenameActiveLayer(_layerName.Text ?? ""));

        var layerActions = new WrapPanel
        {
            Children =
            {
                ActionButton("AddObjectLayerButton", "+ Объекты", _editor.AddObjectLayer),
                ActionButton("AddBrushLayerButton", "+ Кисть", _editor.AddBrushLayer),
                ActionButton("AddLandLayerButton", "+ Земля", _editor.AddLandLayer),
                ActionButton("AddGridLayerButton", "+ Сетка", _editor.AddGridLayer),
                ActionButton("AddFilterLayerButton", "+ Фильтр", _editor.AddFilterLayer),
                ActionButton("LayerUpButton", "↑", () => _editor.ReorderActiveLayer(true)),
                ActionButton("LayerDownButton", "↓", () => _editor.ReorderActiveLayer(false)),
                ActionButton("LayerVisibilityButton", "Показать/скрыть", _editor.ToggleActiveLayerVisibility),
                ActionButton("LayerLockButton", "Замок", _editor.ToggleActiveLayerLock),
                ActionButton("LayerBlendButton", "Blend", _editor.CycleActiveLayerBlend),
                ActionButton("LayerMaskButton", "Маска", _editor.ToggleActiveMask),
                ActionButton("DuplicateLayerButton", "Копия", _editor.DuplicateActiveLayer),
                ActionButton("DeleteLayerButton", "Удалить", _editor.DeleteActiveLayer),
                ActionButton("LandModeButton", "Земля +/−", () =>
                    _editor.LandSubtract = !_editor.LandSubtract),
                ActionButton("LandCircleButton", "Круг", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.Circle)),
                ActionButton("LandRectangleButton", "Прямоуг.", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.Rectangle)),
                ActionButton("LandEllipseButton", "Эллипс", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.Ellipse)),
                ActionButton("LandPolygonButton", "Полигон", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.Polygon)),
                ActionButton("LandGridButton", "Grid-block", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.GridBlock)),
                ActionButton("LandFillButton", "Fill", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.BoundedFill)),
                ActionButton("LandStyleButton", "Стиль берега", _editor.CycleActiveLandStyle),
                ActionButton("LandMoveButton", "Сдвинуть →", () =>
                    _editor.MoveActiveLandArea(128, 0)),
                ActionButton("GridGeometryButton", "Тип сетки", _editor.CycleActiveGridGeometry),
                ActionButton("GridExportButton", "Сетка в экспорт", _editor.ToggleActiveGridExport),
                ActionButton("GridSnapObjectButton", "Snap object", _editor.SnapSelectedObjectToGrid),
                ActionButton("GridSnapNodeButton", "Snap node", _editor.SnapSelectedNodeToGrid),
                ActionButton("GridSnapPortalButton", "Snap portal", _editor.SnapSelectedPortalToGrid),
                ActionButton("FilterStyleButton", "Стиль фильтра", _editor.CycleActiveFilter),
            },
        };

        var layers = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Слои карты", FontWeight = FontWeight.SemiBold },
                new ScrollViewer { MaxHeight = 420, Content = _layerList },
                _layerName,
                rename,
                layerActions,
            },
        };

        _stampObjectGroup = ContextGroup("Stamp", _stampObjectActions);
        _vectorObjectGroup = ContextGroup("Line / Shape", _vectorObjectActions);
        _textObjectGroup = ContextGroup("Text", _textObjectActions);
        _compositionObjectGroup = ContextGroup("Composition", _compositionObjectActions);

        var objects = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 6,
            Children =
            {
                _objectSearch,
                new ScrollViewer { MaxHeight = 220, Content = _objectList },
                ContextGroup("Основное", _commonObjectActions),
                _stampObjectGroup,
                _vectorObjectGroup,
                _textObjectGroup,
                _compositionObjectGroup,
                new Separator(),
                BuildInspector(),
            },
        };

        _commonObjectActions.Children.AddRange(
        [
                        ActionButton("ObjectVisibilityButton", "Показать/скрыть", _editor.ToggleSelectedObjectVisibility),
                        ActionButton("ObjectLockButton", "Замок", _editor.ToggleSelectedObjectLock),
                        ActionButton("DeleteObjectButton", "Удалить", _editor.DeleteSelectedObject),
                        ActionButton("MakeCompositionButton", "Собрать композицию", () => _editor.CreateEditableComposition()),
                        ActionButton("PasteCompositionButton", "Вставить композицию", _editor.PasteComposition),
        ]);
        _stampObjectActions.Children.AddRange(
        [
                        ActionButton("StampFlipButton", "Stamp: отразить", () =>
                            _editor.UpdateSelectedStamp(toggleFlip: true)),
                        ActionButton("StampShadowButton", "Stamp: тень", () =>
                            _editor.UpdateSelectedStamp(toggleShadow: true)),
                        ActionButton("StampHueButton", "Stamp: цвет", () =>
                            _editor.UpdateSelectedStamp(hue: 35, saturation: 0.2)),
        ]);
        _vectorObjectActions.Children.AddRange(
        [
                        ActionButton("AddLineNodeButton", "Line: + node", _editor.AddNodeToSelectedLine),
                        ActionButton("MoveVectorNodeButton", "Node: move", _editor.MoveSelectedVectorNode),
                        ActionButton("CurveNodeButton", "Node: curve", _editor.ToggleSelectedNodeCurve),
                        ActionButton("CutLineButton", "Line: cut", _editor.CutSelectedLine),
                        ActionButton("AddPortalButton", "+ Portal", _editor.AddPortalToSelected),
                        ActionButton("MovePortalButton", "Portal: move", _editor.MoveSelectedPortal),
                        ActionButton("DeletePortalButton", "Portal: delete", _editor.DeleteSelectedPortal),
                        ActionButton("VectorStrokeButton", "Stroke style", _editor.CycleSelectedVectorStroke),
                        ActionButton("AddShapeEllipseButton", "+ Shape ellipse", () =>
                            _editor.AddShape(ShapePrimitiveKind.Ellipse)),
                        ActionButton("BooleanAddButton", "Boolean add", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Add)),
                        ActionButton("BooleanSubtractButton", "Boolean subtract", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Subtract)),
                        ActionButton("BooleanMergeButton", "Boolean merge", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Merge)),
        ]);
        _textObjectActions.Children.AddRange(
        [
                        ActionButton("TextStyleButton", "Text style", _editor.CycleSelectedTextStyle),
                        ActionButton("TextMissingFontButton", "Missing font test", _editor.UseMissingFontOnSelectedText),
        ]);
        _compositionObjectActions.Children.AddRange(
        [
                        ActionButton("CopyCompositionButton", "Копировать композицию", _editor.CopySelectedComposition),
                        ActionButton("FlattenCompositionButton", "Растеризовать", _editor.FlattenSelectedComposition),
        ]);

        var assets = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Локальный каталог · PNG/JPEG/WebP",
                    FontWeight = FontWeight.SemiBold,
                },
                _assetSearch,
                new ScrollViewer { MaxHeight = 420, Content = _assetList },
                new WrapPanel
                {
                    Children =
                    {
                        ActionButton("ImportAssetButton", "Импорт", () => _ = ImportAssetAsync(linked: false)),
                        ActionButton("LinkAssetButton", "Связать", () => _ = ImportAssetAsync(linked: true)),
                        ActionButton("PlaceStampButton", "Разместить", () =>
                        {
                            if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: true);
                        }),
                        ActionButton("PlaceLinkedStampButton", "Разместить linked", () =>
                        {
                            if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: false);
                        }),
                        ActionButton("ScatterStampButton", "Scatter ×100", () =>
                        {
                            if (_selectedAsset is not null) _editor.ScatterStamp(_selectedAsset, 100);
                        }),
                        ActionButton("ReplaceStampAssetButton", "Заменить у stamp", () =>
                        {
                            if (_selectedAsset is not null) _editor.ReplaceSelectedStampAsset(_selectedAsset);
                        }),
                        ActionButton("KeepStampPlaceholderButton", "Оставить placeholder",
                            _editor.KeepSelectedStampPlaceholder),
                        ActionButton("UseAssetStrokeButton", "Asset stroke", () =>
                        {
                            if (_selectedAsset is not null) _editor.UseAssetAsSelectedVectorStroke(_selectedAsset);
                        }),
                        ActionButton("UseAssetFilterButton", "Texture filter", () =>
                        {
                            if (_selectedAsset is not null) _editor.UseAssetAsActiveFilterTexture(_selectedAsset);
                        }),
                    },
                },
                new TextBlock
                {
                    Text = "Linked-файл проверяется по SHA-256; пропажа или подмена видна на холсте.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                },
            },
        };

        var notes = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Локальные заметки · не попадают на карту и в экспорт", TextWrapping = TextWrapping.Wrap },
                _noteSearch,
                new ScrollViewer { MaxHeight = 220, Content = _noteList },
                _noteTitle, _noteMarkdown,
                new WrapPanel
                {
                    Children =
                    {
                        ActionButton("AddWorldNoteButton", "+ В центре", _editor.AddNoteAtViewCentre),
                        ActionButton("AddObjectNoteButton", "+ К объекту", _editor.AddNoteForSelection),
                        ActionButton("SaveNoteButton", "Сохранить", () =>
                        {
                            if (_selectedNoteId is { } id)
                                _editor.EditNote(id, _noteTitle.Text ?? "", _noteMarkdown.Text ?? "");
                        }),
                        ActionButton("GoToNoteButton", "Перейти", () =>
                        {
                            if (_selectedNoteId is { } id) _editor.GoToNote(id);
                        }),
                        ActionButton("DeleteNoteButton", "Удалить", () =>
                        {
                            if (_selectedNoteId is { } id) { _editor.DeleteNote(id); _selectedNoteId = null; }
                        }),
                    },
                },
            },
        };

        return new TabControl
        {
            Name = "EditorSideTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Слои", Content = new ScrollViewer { Content = layers } },
                new TabItem { Header = "Объекты", Content = new ScrollViewer { Content = objects } },
                new TabItem { Header = "Ассеты", Content = new ScrollViewer { Content = assets } },
                new TabItem { Header = "Заметки", Content = new ScrollViewer { Content = notes } },
            },
        };
    }

    private Control BuildInspector()
    {
        static Control Row(string label, TextBox box)
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

    private void SyncSidePanels()
    {
        _layerList.Children.Clear();
        foreach (var layer in _editor.Layers.Reverse())
        {
            var kind = layer switch
            {
                BaseColorLayer => "Фон",
                ObjectLayer => "Объекты",
                BrushLayer => "Кисть",
                LandLayer => "Земля",
                GridLayer => "Сетка",
                FilterLayer => "Фильтр",
                _ => layer.GetType().Name,
            };
            var active = layer.Id == _editor.ActiveLayerId ? "▶ " : "";
            var state = $"{(layer.Visible ? "◉" : "○")} {(layer.Locked ? "🔒" : "")} " +
                        $"{(layer.Mask is null ? "" : layer.Mask.Enabled ? "М" : "М×")}";
            var button = new Button
            {
                Name = "Layer_" + layer.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{active}{state} {layer.Name} · {kind} · {layer.Blend}",
            };
            button.Click += (_, _) =>
            {
                _editor.SelectLayer(layer.Id);
                _layerName.Text = layer.Name;
                SyncSidePanels();
            };
            _layerList.Children.Add(button);
        }

        _layerName.Text = _editor.ActiveLayer?.Name ?? "";
        SyncObjectList();
        SyncAssetList();
        SyncNoteList();
    }

    private void SyncObjectList()
    {
        var query = (_objectSearch.Text ?? "").Trim();
        _objectList.Children.Clear();
        foreach (var sceneObject in _editor.Layers.OfType<ObjectLayer>()
                     .SelectMany(layer => layer.Objects)
                     .Where(item => query.Length == 0
                         || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var selected = sceneObject.Id == _editor.Session.SelectedObjectId ? "▶ " : "";
            var button = new Button
            {
                Name = "Object_" + sceneObject.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{selected}{(sceneObject.Visible ? "◉" : "○")} " +
                          $"{(sceneObject.Locked ? "🔒 " : "")}{sceneObject.Name} · {sceneObject.Kind}",
            };
            button.Click += (_, _) =>
            {
                _editor.SelectObject(sceneObject.Id);
                SyncObjectList();
            };
            _objectList.Children.Add(button);
        }
        SyncObjectActions();
    }

    private void SyncObjectActions()
    {
        var selected = _editor.Session.SelectedObject;
        _commonObjectActions.IsVisible = true; // Paste remains useful even before a selection.
        _stampObjectGroup.IsVisible = selected?.Kind == SceneObjectKind.Stamp;
        _vectorObjectGroup.IsVisible = selected?.Kind is SceneObjectKind.Line or SceneObjectKind.Shape;
        _textObjectGroup.IsVisible = selected?.Kind == SceneObjectKind.Text;
        _compositionObjectGroup.IsVisible = selected?.Kind == SceneObjectKind.Composition;
    }

    private void SyncAssetList()
    {
        var query = (_assetSearch.Text ?? "").Trim();
        _assetList.Children.Clear();
        foreach (var asset in _editor.SearchAssets(query))
        {
            var selected = asset.Sha256 == _selectedAsset?.Sha256 ? "▶ " : "";
            var button = new Button
            {
                Name = "Asset_" + asset.Sha256[..12],
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{selected}{asset.DisplayName} · {asset.Width}×{asset.Height} · " +
                          $"{(asset.LinkedPath is null ? "local" : "linked")}",
            };
            button.Click += (_, _) =>
            {
                _selectedAsset = asset;
                SyncAssetList();
            };
            _assetList.Children.Add(button);
        }
    }

    private void SyncNoteList()
    {
        var query = (_noteSearch.Text ?? "").Trim();
        _noteList.Children.Clear();
        foreach (var note in _editor.SearchNotes(query))
        {
            var selected = note.Id == _selectedNoteId ? "▶ " : "";
            var state = note.IsDetached(_editor.Session.Document) ? " · якорь удалён" : "";
            var button = new Button
            {
                Name = "Note_" + note.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = selected + note.Title + state,
            };
            ToolTip.SetTip(button, "Открыть локальную заметку");
            button.Click += (_, _) =>
            {
                _selectedNoteId = note.Id; _noteTitle.Text = note.Title; _noteMarkdown.Text = note.Markdown;
                SyncNoteList();
            };
            _noteList.Children.Add(button);
        }
    }

    private async System.Threading.Tasks.Task ImportAssetAsync(bool linked)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = linked ? "Связать изображение" : "Импортировать изображение",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                },
            ],
        });
        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        _selectedAsset = _editor.ImportAsset(path, linked);
        SyncAssetList();
        SyncStatus();
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
