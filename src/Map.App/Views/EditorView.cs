using System;
using System.Collections.Generic;
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
    private readonly Dictionary<EditorTool, Button> _toolButtons = [];
    private Control _stampObjectGroup = null!;
    private Control _vectorObjectGroup = null!;
    private Control _textObjectGroup = null!;
    private Control _compositionObjectGroup = null!;

    public EditorView(
        EditorViewModel editor, Func<System.Threading.Tasks.Task> requestBack, Action requestSecondWindow)
    {
        _editor = editor;
        Name = "EditorView";

        Button Tool(string name, string text, Action action, EditorTool? representedTool = null)
        {
            var button = Styled(new Button { Name = name, Content = text, Margin = new Thickness(2) }, "tool");
            ToolTip.SetTip(button, text);
            button.Click += (_, _) =>
            {
                action();
                SyncToolButtons();
            };
            if (representedTool is { } tool)
            {
                _toolButtons[tool] = button;
            }
            return button;
        }

        Button AsyncTool(string name, string text, Func<System.Threading.Tasks.Task> action)
        {
            var button = new Button { Name = name, Content = text, Margin = new Thickness(2) };
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

        var eraser = Tool("EraserButton", "Ластик", () => { });
        eraser.Click += (_, _) =>
        {
            _editor.BrushSettings = _editor.BrushSettings with { Eraser = !_editor.BrushSettings.Eraser };
            SetClass(eraser, "active", _editor.BrushSettings.Eraser);
        };

        var backButton = AsyncTool("BackButton", "← Библиотека", requestBack);
        backButton.Classes.Add("ghost");
        var secondWindowButton = Tool("SecondWindowButton", "Второе окно", requestSecondWindow);
        secondWindowButton.Classes.Add("ghost");
        var saveButton = AsyncTool("SaveButton", "Сохранить", () => _editor.SaveAsync());
        saveButton.Classes.Add("primary");
        var saveAsButton = AsyncTool("SaveAsButton", "Сохранить как…", () => _editor.SaveAsync(saveAs: true));
        var fitButton = Tool("FitButton", "Вписать карту", _canvas.FitToCanvas);

        var primaryToolbar = new DockPanel
        {
            Children =
            {
                WithDock(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children = { saveAsButton, saveButton },
                }, Dock.Right),
                WithDock(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children = { backButton, secondWindowButton },
                }, Dock.Left),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Margin = new Thickness(18, 0, 0, 0),
                    Children =
                    {
                        Tool("SelectToolButton", "Выбор", () => _editor.ActiveTool = EditorTool.Select, EditorTool.Select),
                        Tool("UndoButton", "Отменить", _editor.Undo),
                        Tool("RedoButton", "Повторить", _editor.Redo),
                        fitButton,
                    },
                },
            },
        };
        var objectTools = new WrapPanel
        {
            Children =
            {
                Tool("LineToolButton", "Линия", () => _editor.ActiveTool = EditorTool.Line, EditorTool.Line),
                Tool("ShapeToolButton", "Фигура", () => _editor.ActiveTool = EditorTool.Shape, EditorTool.Shape),
                Tool("TextToolButton", "Текст", () => _editor.ActiveTool = EditorTool.Text, EditorTool.Text),
                Tool("AddRectangleButton", "+ Прямоугольник", () => _editor.AddObject(SceneObjectKind.Rectangle)),
                Tool("AddEllipseButton", "+ Эллипс", () => _editor.AddObject(SceneObjectKind.Ellipse)),
                Tool("AddLineButton", "+ Линия", _editor.AddLine),
                Tool("AddVectorRectangleButton", "+ Фигура", () => _editor.AddShape(ShapePrimitiveKind.Rectangle)),
                Tool("AddTextButton", "+ Текст", _editor.AddText),
            },
        };
        var rasterTools = new WrapPanel
        {
            Children =
            {
                Tool("BrushToolButton", "Кисть", () => _editor.ActiveTool = EditorTool.Brush, EditorTool.Brush),
                Tool("MaskToolButton", "Маска", () => _editor.ActiveTool = EditorTool.Mask, EditorTool.Mask),
                Tool("LandToolButton", "Земля", () => _editor.ActiveTool = EditorTool.Land, EditorTool.Land),
                brushSize, brushOpacity, eraser,
                Tool("MaskModeButton", "Маска +/−", () =>
                    _editor.MaskMode = _editor.MaskMode == Map.Core.Editing.MaskPaintMode.Hide
                        ? Map.Core.Editing.MaskPaintMode.Reveal
                        : Map.Core.Editing.MaskPaintMode.Hide),
            },
        };
        var toolPalette = new TabControl
        {
            Name = "ToolPaletteTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Объекты", Content = objectTools },
                new TabItem { Header = "Кисти и маски", Content = rasterTools },
            },
        };
        var toolbar = new StackPanel
        {
            Children =
            {
                Styled(new Border
                {
                    Padding = new Thickness(12, 7),
                    Child = primaryToolbar,
                }, "top-bar"),
                Styled(new Border
                {
                    Padding = new Thickness(10, 0, 10, 6),
                    Child = toolPalette,
                }, "tool-strip"),
            },
        };

        var rightPanel = BuildRightPanel();

        var statusBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Margin = new Thickness(12, 7),
            Children = { _saveState, _selectionStatus, _viewportStatus, _statusDetail },
        };
        _selectionStatus.Classes.Add("muted");
        _viewportStatus.Classes.Add("muted");
        _statusDetail.Classes.Add("muted");

        Content = new DockPanel
        {
            Children =
            {
                WithDock(toolbar, Dock.Top),
                WithDock(Styled(new Border
                {
                    Child = statusBar,
                }, "status-bar"), Dock.Bottom),
                WithDock(Styled(new Border
                {
                    Width = 326,
                    Child = rightPanel,
                }, "side-panel"), Dock.Right),
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
        SyncToolButtons();

        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(_canvas.FitToCanvas, DispatcherPriority.Loaded);
    }

    public EditorViewModel Editor => _editor;

    private Control BuildRightPanel()
    {
        Button ActionButton(string name, string text, Action action, string? styleClass = null)
        {
            var button = Styled(new Button { Name = name, Content = text, Margin = new Thickness(2) }, "tool");
            if (styleClass is not null)
            {
                button.Classes.Add(styleClass);
            }
            ToolTip.SetTip(button, text);
            button.Click += (_, _) => action();
            return button;
        }

        static Control ContextGroup(string title, Control content) => new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = title, Classes = { "section-title" } },
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
                ActionButton("LayerBlendButton", "Наложение", _editor.CycleActiveLayerBlend),
                ActionButton("LayerMaskButton", "Маска", _editor.ToggleActiveMask),
                ActionButton("DuplicateLayerButton", "Копия", _editor.DuplicateActiveLayer),
                ActionButton("DeleteLayerButton", "Удалить", _editor.DeleteActiveLayer, "danger"),
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
                ActionButton("LandGridButton", "Блок по сетке", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.GridBlock)),
                ActionButton("LandFillButton", "Заливка области", () =>
                    _editor.ApplyLandShape(Map.Core.Editing.LandShapeKind.BoundedFill)),
                ActionButton("LandStyleButton", "Стиль берега", _editor.CycleActiveLandStyle),
                ActionButton("LandMoveButton", "Сдвинуть →", () =>
                    _editor.MoveActiveLandArea(128, 0)),
                ActionButton("GridGeometryButton", "Тип сетки", _editor.CycleActiveGridGeometry),
                ActionButton("GridExportButton", "Сетка в экспорт", _editor.ToggleActiveGridExport),
                ActionButton("GridSnapObjectButton", "Привязать объект", _editor.SnapSelectedObjectToGrid),
                ActionButton("GridSnapNodeButton", "Привязать узел", _editor.SnapSelectedNodeToGrid),
                ActionButton("GridSnapPortalButton", "Привязать портал", _editor.SnapSelectedPortalToGrid),
                ActionButton("FilterStyleButton", "Стиль фильтра", _editor.CycleActiveFilter),
            },
        };

        var layers = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Слои карты", Classes = { "section-title" } },
                new ScrollViewer { MaxHeight = 420, Content = _layerList },
                _layerName,
                rename,
                layerActions,
            },
        };

        _stampObjectGroup = ContextGroup("Штамп", _stampObjectActions);
        _vectorObjectGroup = ContextGroup("Линия и фигура", _vectorObjectActions);
        _textObjectGroup = ContextGroup("Текст", _textObjectActions);
        _compositionObjectGroup = ContextGroup("Композиция", _compositionObjectActions);

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
                        ActionButton("DeleteObjectButton", "Удалить", _editor.DeleteSelectedObject, "danger"),
                        ActionButton("MakeCompositionButton", "Собрать композицию", () => _editor.CreateEditableComposition()),
                        ActionButton("PasteCompositionButton", "Вставить композицию", _editor.PasteComposition),
        ]);
        _stampObjectActions.Children.AddRange(
        [
                        ActionButton("StampFlipButton", "Отразить", () =>
                            _editor.UpdateSelectedStamp(toggleFlip: true)),
                        ActionButton("StampShadowButton", "Тень", () =>
                            _editor.UpdateSelectedStamp(toggleShadow: true)),
                        ActionButton("StampHueButton", "Цвет", () =>
                            _editor.UpdateSelectedStamp(hue: 35, saturation: 0.2)),
        ]);
        _vectorObjectActions.Children.AddRange(
        [
                        ActionButton("AddLineNodeButton", "Добавить узел", _editor.AddNodeToSelectedLine),
                        ActionButton("MoveVectorNodeButton", "Сдвинуть узел", _editor.MoveSelectedVectorNode),
                        ActionButton("CurveNodeButton", "Прямая / кривая", _editor.ToggleSelectedNodeCurve),
                        ActionButton("CutLineButton", "Разрезать линию", _editor.CutSelectedLine),
                        ActionButton("AddPortalButton", "+ Портал", _editor.AddPortalToSelected),
                        ActionButton("MovePortalButton", "Сдвинуть портал", _editor.MoveSelectedPortal),
                        ActionButton("DeletePortalButton", "Удалить портал", _editor.DeleteSelectedPortal, "danger"),
                        ActionButton("VectorStrokeButton", "Стиль обводки", _editor.CycleSelectedVectorStroke),
                        ActionButton("AddShapeEllipseButton", "+ Эллипс", () =>
                            _editor.AddShape(ShapePrimitiveKind.Ellipse)),
                        ActionButton("BooleanAddButton", "Объединить", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Add)),
                        ActionButton("BooleanSubtractButton", "Вычесть", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Subtract)),
                        ActionButton("BooleanMergeButton", "Слить контуры", () =>
                            _editor.ApplyShapeBoolean(Map.Core.Editing.ShapeBooleanOperation.Merge)),
        ]);
        _textObjectActions.Children.AddRange(
        [
                        ActionButton("TextStyleButton", "Стиль текста", _editor.CycleSelectedTextStyle),
                        ActionButton("TextMissingFontButton", "Проверить замену шрифта", _editor.UseMissingFontOnSelectedText),
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
                    Classes = { "section-title" },
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
                        ActionButton("PlaceLinkedStampButton", "Разместить связанный", () =>
                        {
                            if (_selectedAsset is not null) _editor.PlaceStamp(_selectedAsset, embedded: false);
                        }),
                        ActionButton("ScatterStampButton", "Рассеять ×100", () =>
                        {
                            if (_selectedAsset is not null) _editor.ScatterStamp(_selectedAsset, 100);
                        }),
                        ActionButton("ReplaceStampAssetButton", "Заменить у штампа", () =>
                        {
                            if (_selectedAsset is not null) _editor.ReplaceSelectedStampAsset(_selectedAsset);
                        }),
                        ActionButton("KeepStampPlaceholderButton", "Оставить заменитель",
                            _editor.KeepSelectedStampPlaceholder),
                        ActionButton("UseAssetStrokeButton", "Использовать для обводки", () =>
                        {
                            if (_selectedAsset is not null) _editor.UseAssetAsSelectedVectorStroke(_selectedAsset);
                        }),
                        ActionButton("UseAssetFilterButton", "Использовать в фильтре", () =>
                        {
                            if (_selectedAsset is not null) _editor.UseAssetAsActiveFilterTexture(_selectedAsset);
                        }),
                    },
                },
                new TextBlock
                {
                    Text = "Связанный файл проверяется по SHA-256; пропажа или подмена видна на холсте.",
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "muted" },
                },
            },
        };

        var notes = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Локальные заметки · не попадают на карту и в экспорт",
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "muted" },
                },
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
                        }, "danger"),
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

        var apply = Styled(new Button
        {
            Name = "ApplyButton", Content = "Применить", Margin = new Thickness(0, 8, 0, 0),
        }, "primary");
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
                new TextBlock
                {
                    Text = "Свойства объекта",
                    Classes = { "section-title" },
                    Margin = new Thickness(0, 0, 0, 6),
                },
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
            if (e.PropertyName == nameof(EditorViewModel.ActiveTool))
            {
                SyncToolButtons();
            }
        });

    private void SyncToolButtons()
    {
        foreach (var pair in _toolButtons)
        {
            SetClass(pair.Value, "active", pair.Key == _editor.ActiveTool);
        }
    }

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
            var isActive = layer.Id == _editor.ActiveLayerId;
            var state = layer.Visible ? "●" : "○";
            var button = Styled(new Button
            {
                Name = "Layer_" + layer.Id,
                Content = $"{state}  {layer.Name}{(layer.Locked ? " · заблокирован" : "")}",
            }, "list-item");
            SetClass(button, "selected", isActive);
            ToolTip.SetTip(button, $"{kind} · наложение: {layer.Blend}" +
                                   (layer.Mask is null ? "" : layer.Mask.Enabled ? " · маска включена" : " · маска выключена"));
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
            var selected = sceneObject.Id == _editor.Session.SelectedObjectId;
            var button = Styled(new Button
            {
                Name = "Object_" + sceneObject.Id,
                Content = $"{(sceneObject.Visible ? "●" : "○")}  {sceneObject.Name}" +
                          (sceneObject.Locked ? " · заблокирован" : ""),
            }, "list-item");
            SetClass(button, "selected", selected);
            ToolTip.SetTip(button, $"Тип: {sceneObject.Kind}");
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
            var selected = asset.Sha256 == _selectedAsset?.Sha256;
            var button = Styled(new Button
            {
                Name = "Asset_" + asset.Sha256[..12],
                Content = $"{asset.DisplayName} · {asset.Width}×{asset.Height}",
            }, "list-item");
            SetClass(button, "selected", selected);
            ToolTip.SetTip(button, asset.LinkedPath is null ? "Хранится в проекте" : "Связанный файл");
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
            var selected = note.Id == _selectedNoteId;
            var state = note.IsDetached(_editor.Session.Document) ? " · якорь удалён" : "";
            var button = Styled(new Button
            {
                Name = "Note_" + note.Id,
                Content = note.Title + state,
            }, "list-item");
            SetClass(button, "selected", selected);
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

    private static T Styled<T>(T control, params string[] classes) where T : StyledElement
    {
        foreach (var className in classes)
        {
            control.Classes.Add(className);
        }

        return control;
    }

    private static void SetClass(StyledElement element, string className, bool enabled)
    {
        element.Classes.Remove(className);
        if (enabled)
        {
            element.Classes.Add(className);
        }
    }

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
