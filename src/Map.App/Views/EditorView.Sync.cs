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
    // ==================================================================== sync

    private void OnShortcutsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncShortcutSurfaces);

    private void SyncShortcutSurfaces()
    {
        SetCommandTip(_undoButton, "Отменить", EditorShortcutSettings.Commands.Undo);
        SetCommandTip(_redoButton, "Повторить", EditorShortcutSettings.Commands.Redo);
        SetCommandTip(_saveButton, "Сохранить проект", EditorShortcutSettings.Commands.Save);
        SetCommandTip(_shortcutSettingsButton, "Настроить сочетания", EditorShortcutSettings.Commands.ShortcutSettings);

        foreach (var (tool, button) in _toolButtons)
        {
            var title = EditorLabels.Tool(tool);
            var shortcut = ToolShortcut(tool);
            var text = shortcut.Length > 0 ? $"{title}  ({shortcut})" : title;
            AutomationProperties.SetName(button, title);
            AutomationProperties.SetHelpText(button, text);
            ToolTip.SetTip(button, text);
        }

        if (_previousVariantButton is not null)
            SetCommandTip(_previousVariantButton, "Предыдущий вариант", EditorShortcutSettings.Commands.StampPreviousVariant);
        if (_nextVariantButton is not null)
            SetCommandTip(_nextVariantButton, "Следующий вариант", EditorShortcutSettings.Commands.StampNextVariant);
        foreach (var item in _commandButtons)
            SetCommandTip(item.Button, item.Label, item.Command);
        foreach (var sync in _shortcutSurfaceSyncs)
            sync();
    }

    private void SetCommandTip(Button button, string label, string command)
    {
        if (button is null) return;
        var shortcut = _editor.Shortcuts.Get(command);
        var text = shortcut.Length > 0 ? $"{label}  ({shortcut})" : label;
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, text);
        ToolTip.SetTip(button, text);
    }

    private string ToolShortcut(EditorTool tool) =>
        _editor.Shortcuts.Get(EditorShortcutSettings.ToolCommand(tool));

    private Button ShortcutTextButton(
        string name,
        string label,
        string command,
        Action action,
        params string[] classes)
    {
        var button = Ui.TextButton(name, label, action, classes);
        _shortcutSurfaceSyncs.Add(() =>
        {
            var gesture = _editor.Shortcuts.Get(command);
            var text = gesture.Length > 0 ? $"{label} ({gesture})" : label;
            if (button.Content is TextBlock caption) caption.Text = text;
            AutomationProperties.SetName(button, text);
            AutomationProperties.SetHelpText(button, text);
            ToolTip.SetTip(button, text);
        });
        return button;
    }

    private void DynamicTip(Control control, Func<string> text)
    {
        _shortcutSurfaceSyncs.Add(() =>
        {
            var value = text();
            AutomationProperties.SetHelpText(control, value);
            ToolTip.SetTip(control, value);
        });
    }

    private readonly HashSet<string> _pendingUiProperties = [];
    private readonly object _uiSyncGate = new();
    private bool _uiSyncQueued;
    private bool _sceneSyncPending;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.HexPresentation)) Canvas.InvalidateVisual();
        // Cursor-only ghost movement is handled by the canvas; it cannot rebuild the tool panel.
        if (e.PropertyName is nameof(EditorViewModel.PendingPlacement) or nameof(EditorViewModel.PlacementPreviewObjects)) return;
        lock (_uiSyncGate)
        {
            _pendingUiProperties.Add(e.PropertyName ?? "");
            if (_uiSyncQueued) return;
            _uiSyncQueued = true;
        }
        Dispatcher.UIThread.Post(FlushEditorUi, DispatcherPriority.Render);
    }

    private void FlushEditorUi()
    {
        HashSet<string> changed;
        bool scene;
        lock (_uiSyncGate)
        {
            changed = [.. _pendingUiProperties];
            _pendingUiProperties.Clear();
            _uiSyncQueued = false;
            scene = _sceneSyncPending;
            _sceneSyncPending = false;
        }
        // Diagnostics only (native profiles): how long each panel sync took on the UI thread.
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        // An edit that leaves the tool, the selection and the creation modes as they were (a placed
        // stamp, a released stroke, undo) only changes what the panels list. Those panels cost
        // 15-40 ms together and ran right before the next frame, so every click dropped frames;
        // they now follow a moment later, one at a time, between input and render work.
        var structure = UiStructureKey();
        var deferPanels = scene && structure == _lastUiStructure;
        _lastUiStructure = structure;
        Timed("status", SyncStatus);
        Timed("stamp-strip", SyncStampStrip);
        if (deferPanels)
        {
            DeferPanels(DeferredPanel.Path | DeferredPanel.Area | DeferredPanel.River | DeferredPanel.Road
                | DeferredPanel.Side | DeferredPanel.Context);
        }
        else
        {
            Timed("path-panel", SyncEditablePathPanel);
            Timed("area-panel", SyncEditableAreaPanel);
            Timed("river-panel", SyncRiverPanel);
            Timed("road-panel", SyncRoadPanel);
            _deferredPanels &= ~(DeferredPanel.Path | DeferredPanel.Area | DeferredPanel.River | DeferredPanel.Road);
            if (scene)
            {
                Timed("side-panels", SyncSidePanels);
                _deferredPanels &= ~DeferredPanel.Side;
            }
        }
        // Water, roads and land are selections too: the section that lists them opens, its
        // property block refreshes, and the rows show the selected state.
        if (changed.Overlaps([nameof(EditorViewModel.WaterSelectionIds), nameof(EditorViewModel.RoadSelectionIds),
            nameof(EditorViewModel.SelectedLand), nameof(EditorViewModel.SelectionCount)]))
        {
            Timed("follow-selection", FollowSelectionToSection);
            Timed("object-list-selection", SyncObjectListSelection);
            Timed("section-footer", SyncSectionFooter);
        }
        if (changed.Overlaps([nameof(EditorViewModel.XText), nameof(EditorViewModel.YText),
            nameof(EditorViewModel.WidthText), nameof(EditorViewModel.HeightText),
            nameof(EditorViewModel.RotationText), nameof(EditorViewModel.HasEditableSelection)])) Timed("inspector", SyncInspector);
        if (!deferPanels && (scene || changed.Overlaps([nameof(EditorViewModel.ActiveTool), nameof(EditorViewModel.SelectionKind),
            nameof(EditorViewModel.SelectionCount), nameof(EditorViewModel.SelectedGroupId),
            nameof(EditorViewModel.ActiveLayer), nameof(EditorViewModel.HasPendingPlacement),
            nameof(EditorViewModel.PlacementAutoSelect), nameof(EditorViewModel.StampPlacementMode)])))
        {
            Timed("context-area", SyncContextArea);
            Timed("gesture-readout", SyncStampGestureReadout);
            Timed("quick-bar", SyncQuickBar);
            _deferredPanels &= ~DeferredPanel.Context;
        }
        if (changed.Overlaps([nameof(EditorViewModel.StampToolSettings), nameof(EditorViewModel.CurrentVariant),
            nameof(EditorViewModel.CompactRecentSets), nameof(EditorViewModel.CompactFavoriteSets)]))
        {
            Timed("appearance-sections", SyncAppearanceSections);
            Timed("stamp-tool-panel", SyncStampToolPanel);
        }
        if (changed.Any(p => p.StartsWith("Canvas", StringComparison.Ordinal) || p.StartsWith("Region", StringComparison.Ordinal)))
            Timed("canvas-workflow", SyncCanvasWorkflow);
        if (changed.Any(p => p.StartsWith("Export", StringComparison.Ordinal)) || changed.Overlaps([
            nameof(EditorViewModel.CurrentExportPlan), nameof(EditorViewModel.CanStartExport),
            nameof(EditorViewModel.CanCancelExport), nameof(EditorViewModel.CanOpenExportFolder)])) Timed("export", SyncExport);
    }

    [Flags]
    private enum DeferredPanel { None = 0, Path = 1, Area = 2, River = 4, Road = 8, Side = 16, Context = 32 }

    private DeferredPanel _deferredPanels;
    private bool _deferredPanelQueued;
    private string? _lastUiStructure;

    /// <summary>What decides which panels and inspectors are mounted: tool, selection, active layer, creation modes.</summary>
    private string UiStructureKey() => string.Join('|',
        _editor.ActiveTool, _editor.SelectionKind, _editor.SelectionCount, _editor.Session.SelectedObjectId,
        _editor.SelectedGroupId, _editor.OpenGroupId, _editor.ActiveLayerId, _editor.HasPendingPlacement,
        _editor.StampPlacementMode, _editor.LandPaintMode,
        _editor.WaterSelectionCount, _editor.SelectedRiver?.Id, _editor.IsCreatingRiver,
        _editor.RoadSelectionCount, _editor.SelectedRoad?.Id, _editor.IsCreatingRoad,
        _editor.SelectedEditablePath?.Id, _editor.IsCreatingEditablePath,
        _editor.SelectedEditableArea?.Id, _editor.IsCreatingEditableArea);

    private DispatcherTimer? _deferredPanelTimer;

    /// <summary>
    /// Queues the listed panels for a refresh once the edits pause for a moment. A slider drag or a
    /// burst of placements edits the document 60 times a second; refreshing the context and side
    /// panels after every one of those edits (15-20 ms of UI thread each on the owner's test map)
    /// held a cliff slider at about 40 FPS. The panels show the result of the last edit as soon
    /// as the stream of edits stops.
    /// </summary>
    private void DeferPanels(DeferredPanel panels)
    {
        _deferredPanels |= panels;
        if (_deferredPanelQueued) return;
        if (_deferredPanelTimer is null)
        {
            // 250 ms: placement clicks come about every 130 ms; with 50 000 objects the side and
            // surface panels cost 20-25 ms per refresh, which landed between every two clicks.
            _deferredPanelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _deferredPanelTimer.Tick += (_, _) =>
            {
                _deferredPanelTimer.Stop();
                if (_deferredPanelQueued) return;
                _deferredPanelQueued = true;
                Dispatcher.UIThread.Post(RunDeferredPanel, DispatcherPriority.Background);
            };
        }
        _deferredPanelTimer.Stop();
        _deferredPanelTimer.Start();
    }

    /// <summary>
    /// Refreshes one deferred panel and queues the next, so no single dispatcher job holds the
    /// UI thread for more than a few milliseconds; input and frames go first (Background priority).
    /// </summary>
    private void RunDeferredPanel()
    {
        _deferredPanelQueued = false;
        var next = DeferredPanel.None;
        foreach (var panel in (ReadOnlySpan<DeferredPanel>)[DeferredPanel.Context, DeferredPanel.Side, DeferredPanel.Path,
                     DeferredPanel.Area, DeferredPanel.River, DeferredPanel.Road])
        {
            if ((_deferredPanels & panel) != 0) { next = panel; break; }
        }
        if (next == DeferredPanel.None) return;
        _deferredPanels &= ~next;

        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        switch (next)
        {
            case DeferredPanel.Context:
                SyncContextArea();
                SyncStampGestureReadout();
                SyncQuickBar();
                break;
            case DeferredPanel.Side: SyncSidePanels(); break;
            case DeferredPanel.Path: SyncEditablePathPanel(); break;
            case DeferredPanel.Area: SyncEditableAreaPanel(); break;
            case DeferredPanel.River: SyncRiverPanel(); break;
            case DeferredPanel.Road: SyncRoadPanel(); break;
        }
        timings?.Add(("ui:deferred/" + next.ToString().ToLowerInvariant(), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        if (_deferredPanels != DeferredPanel.None)
        {
            _deferredPanelQueued = true;
            Dispatcher.UIThread.Post(RunDeferredPanel, DispatcherPriority.Background);
        }
    }

    private void SyncCanvasWorkflow()
    {
        if (_resizeModeButton is null || _regionModeButton is null
            || _resizeWorkflowPanel is null || _regionWorkflowPanel is null
            || _boundarySnapToggle is null)
        {
            return;
        }

        _syncingCanvasWorkflow = true;
        try
        {
            SetText(_canvasWidthBox, _editor.CanvasWidthText);
            SetText(_canvasHeightBox, _editor.CanvasHeightText);
            SetText(_regionXBox, _editor.RegionXText);
            SetText(_regionYBox, _editor.RegionYText);
            SetText(_regionWidthBox, _editor.RegionWidthText);
            SetText(_regionHeightBox, _editor.RegionHeightText);
            SetText(_regionTitleBox, _editor.RegionTitleText);

            Ui.SetSelected(_resizeModeButton, _editor.CanvasWorkflowMode == ResizeRegionMode.Resize);
            Ui.SetSelected(_regionModeButton, _editor.CanvasWorkflowMode == ResizeRegionMode.Region);
            _resizeWorkflowPanel.IsVisible = _editor.CanvasWorkflowMode == ResizeRegionMode.Resize;
            _regionWorkflowPanel.IsVisible = _editor.CanvasWorkflowMode == ResizeRegionMode.Region;
            _boundarySnapToggle.IsChecked = _editor.CanvasBoundarySnap;
            Ui.SetClass(_boundarySnapToggle, "active", _editor.CanvasBoundarySnap);

            foreach (var (anchor, button) in _anchorButtons)
            {
                Ui.SetSelected(button, anchor == _editor.CanvasAnchor);
            }

            _canvasWorkflowSummary.Text = _editor.CanvasWorkflowSummary;
            _canvasObjectSummary.Text = _editor.CanvasObjectSummary;
            _canvasWorkflowError.Text = _editor.CanvasWorkflowError;
            _canvasWorkflowError.IsVisible = _editor.CanvasWorkflowError.Length > 0;
        }
        finally
        {
            _syncingCanvasWorkflow = false;
        }

        static void SetText(TextBox box, string value)
        {
            if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                box.Text = value;
            }
        }
    }

    private void SyncExport()
    {
        if (_vttSummaryText is not null) _vttSummaryText.Text = _editor.ExportVttSummary;
        if (_startExportButton is null || _cancelExportButton is null
            || _openExportFolderButton is null || _exportAspectToggle is null
            || _exportTransparencyToggle is null || _exportGridToggle is null
            || _exportQualitySlider is null || _exportRegionPanel is null
            || _exportQualityPanel is null)
        {
            return;
        }

        _syncingExport = true;
        try
        {
            SetText(_exportRegionXBox, _editor.ExportRegionXText);
            SetText(_exportRegionYBox, _editor.ExportRegionYText);
            SetText(_exportRegionWidthBox, _editor.ExportRegionWidthText);
            SetText(_exportRegionHeightBox, _editor.ExportRegionHeightText);
            SetText(_exportOutputWidthBox, _editor.ExportOutputWidthText);
            SetText(_exportOutputHeightBox, _editor.ExportOutputHeightText);

            foreach (var (format, button) in _exportFormatButtons)
                Ui.SetSelected(button, format == _editor.ExportFormat);
            foreach (var (area, button) in _exportAreaButtons)
                Ui.SetSelected(button, area == _editor.ExportAreaMode);
            foreach (var (mode, button) in _exportModeButtons)
                Ui.SetSelected(button, mode == _editor.ExportOutputMode);
            foreach (var (scale, button) in _exportScaleButtons)
                Ui.SetSelected(button, scale == _editor.ExportScaleFactor);

            _exportRegionPanel.IsVisible = _editor.ExportAreaMode == ExportAreaMode.Region;
            _exportQualityPanel.IsVisible = _editor.ExportQualityVisible;
            _exportAspectToggle.IsChecked = _editor.ExportAspectLocked;
            _exportTransparencyToggle.IsChecked = _editor.ExportTransparentBackground;
            _exportTransparencyToggle.IsEnabled = _editor.ExportTransparencyAvailable
                && !_editor.ExportRunning;
            _exportGridToggle.IsChecked = _editor.ExportIncludeGrid;
            _exportQualitySlider.Value = _editor.ExportQuality;
            _exportQualityText.Text = $"Качество: {_editor.ExportQuality}";
            _exportFormatExplanation.Text = _editor.ExportFormatExplanation;
            _exportEstimate.Text = _editor.ExportEstimateText;
            _exportDetailSummary.Text = _editor.ExportDetailSummary;
            _exportMemoryDetails.Text = _editor.ExportMemoryDetails;
            _exportError.Text = _editor.ExportValidationError;
            _exportError.IsVisible = _editor.ExportValidationError.Length > 0;
            _exportProgress.Value = _editor.ExportProgress * 100;
            _exportProgress.IsVisible = _editor.ExportRunning || _editor.ExportProgress > 0;
            _exportProgressText.Text = _editor.ExportPreviewBusy
                ? "Обновление предпросмотра…"
                : _editor.ExportProgressText;
            _exportProgressText.IsVisible = _exportProgressText.Text?.Length > 0;
            _exportResult.Text = _editor.ExportResultText;
            _exportResult.IsVisible = _editor.ExportResultText.Length > 0;

            if (!ReferenceEquals(_shownExportPreviewPng, _editor.ExportPreviewPng))
            {
                _shownExportPreviewPng = _editor.ExportPreviewPng;
                _exportPreviewBitmap?.Dispose();
                _exportPreviewBitmap = null;
                _exportPreview.Source = null;
                if (_shownExportPreviewPng is { Length: > 0 } bytes)
                {
                    try
                    {
                        using var stream = new MemoryStream(bytes, writable: false);
                        _exportPreviewBitmap = new Bitmap(stream);
                        _exportPreview.Source = _exportPreviewBitmap;
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException)
                    {
                        _exportPreview.Source = null;
                    }
                }
            }

            var settingsEnabled = !_editor.ExportRunning;
            foreach (var button in _exportFormatButtons.Values) button.IsEnabled = settingsEnabled;
            foreach (var button in _exportAreaButtons.Values) button.IsEnabled = settingsEnabled;
            foreach (var button in _exportModeButtons.Values) button.IsEnabled = settingsEnabled;
            foreach (var button in _exportScaleButtons.Values) button.IsEnabled = settingsEnabled;
            _exportRegionXBox.IsEnabled = settingsEnabled;
            _exportRegionYBox.IsEnabled = settingsEnabled;
            _exportRegionWidthBox.IsEnabled = settingsEnabled;
            _exportRegionHeightBox.IsEnabled = settingsEnabled;
            _exportOutputWidthBox.IsEnabled = settingsEnabled;
            _exportOutputHeightBox.IsEnabled = settingsEnabled;
            _exportAspectToggle.IsEnabled = settingsEnabled;
            _exportGridToggle.IsEnabled = settingsEnabled;
            _exportQualitySlider.IsEnabled = settingsEnabled;
            _startExportButton.IsEnabled = _editor.CanStartExport;
            _cancelExportButton.IsEnabled = _editor.CanCancelExport;
            _cancelExportButton.IsVisible = _editor.ExportRunning;
            _openExportFolderButton.IsEnabled = _editor.CanOpenExportFolder;
            _openExportFolderButton.IsVisible = _editor.ExportResultPath is not null;
            _viewExportedPngButton.IsVisible = !_editor.ExportRunning
                && string.Equals(Path.GetExtension(_editor.ExportResultPath), ".png", StringComparison.OrdinalIgnoreCase)
                && File.Exists(_editor.ExportResultPath);
        }
        finally
        {
            _syncingExport = false;
        }

        static void SetText(TextBox box, string value)
        {
            if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                box.Text = value;
            }
        }
    }

    private void SyncStatus()
    {
        _saveState.Text = _editor.PrimaryAutosaveEnabled
            ? _editor.SaveSummary
            : _editor.SaveSummary + " · автосохранение выключено";
        ToolTip.SetTip(_saveState, _editor.PrimaryAutosaveSummary);
        Ui.SetClass(_saveState, "danger", _editor.SaveState == SaveState.SaveFailed);
        Ui.SetClass(_saveState, "success", _editor.SaveState == SaveState.Saved);
        Ui.SetClass(_saveState, "warn", _editor.SaveState == SaveState.Unsaved);

        _autosaveState.Text = _editor.AutosaveSummary;
        _autosaveState.IsVisible = _editor.AutosaveSummary != "Восстановление не требуется";
        Ui.SetClass(_autosaveState, "danger", _editor.AutosaveState == AutosaveState.Failed);
        Ui.SetClass(_autosaveState, "success", _editor.AutosaveState == AutosaveState.Saved);

        ShowStatusToast(_editor.StatusDetail);

        _selectionStatus.Text = _editor.SelectionStatus;
        ToolTip.SetTip(_selectionStatus, _editor.SelectionStatus);
        _activeLayerStatus.Text = _editor.ActiveLayerSummary;
        ToolTip.SetTip(_activeLayerStatus, _editor.ActiveLayerSummary);
        _gridStatus.Text = _editor.GridSummary;
        _viewportStatus.Text = _editor.ViewportStatus;
        SyncMapStatistics();
        _inspectorError.Text = _editor.InspectorError;
        _inspectorError.IsVisible = _editor.InspectorError.Length > 0;

        _undoButton.IsEnabled = _editor.CanUndo;
        _redoButton.IsEnabled = _editor.CanRedo;
    }

    private void SyncInspector()
    {
        _xBox.Text = _editor.XText;
        _yBox.Text = _editor.YText;
        _widthBox.Text = _editor.WidthText;
        _heightBox.Text = _editor.HeightText;
        _rotationBox.Text = _editor.RotationText;

        var editable = _editor.HasEditableSelection;
        foreach (var box in new[] { _xBox, _yBox, _widthBox, _heightBox, _rotationBox })
        {
            box.IsEnabled = editable;
        }

    }

    /// <summary>
    /// The two halves of the context area, named so the user can move between them. A selection
    /// opens its inspector, but the tool it was drawn with stays one click away - hiding the tool
    /// would be as wrong as hiding the object, which is what the panel used to do.
    /// </summary>
    private void BuildContextSwitch()
    {
        var (control, buttons) = Ui.Segmented(
            "ContextScope",
            [("Object", "Объект", null), ("Tool", "Инструмент", null)],
            key =>
            {
                _contextPrefersToolPanel = key == "Tool";
                SyncContextArea();
            });

        _contextSwitchButtons.AddRange(buttons);
        _contextSwitch = control;
    }

    /// <summary>
    /// Decides what the single context area shows. Exactly one of two things: the settings of the
    /// active tool, or the properties of the current selection - never both, and never the
    /// properties of a kind that is not selected. Which one is the user's own choice through the
    /// scope switch, defaulting to the object right after a selection and to the tool right after
    /// a tool switch, so both actions always have a visible effect.
    /// </summary>
    private void SyncContextArea()
    {
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:context/" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        foreach (var pair in _toolButtons)
        {
            Ui.SetToolActive(pair.Value, pair.Key == _editor.ActiveTool);
        }

        Timed("land-buttons", SyncLandButtons);
        Timed("mask-buttons", SyncMaskModeButtons);
        Timed("layer-blocks", SyncLayerBlocks);
        Timed("appearance", SyncAppearanceSections);
        // The strip is refreshed on every context change, including "nothing is selected" - the
        // panel's own refresh only runs while an inspector is mounted.
        Timed("quick-bar", SyncQuickBar);
        // A new selection points the panel at the object; a new tool points it at the tool. Both
        // remain reachable afterwards, which is the part the old ActiveTool == Select gate broke.
        var selectionSignature = _editor.SelectionCount > 0
            ? $"{_editor.SelectionCount}:{_editor.Session.SelectedObjectId}"
            : _editor.WaterSelectionCount > 0 ? "water:" + _editor.SelectedRiver?.Id
            : _editor.RoadSelectionCount > 0 ? "road:" + _editor.SelectedRoad?.Id
            : null;
        if (_lastContextTool != _editor.ActiveTool)
        {
            _lastContextTool = _editor.ActiveTool;
            // Choosing the river or road tool with a river or road already selected (from the
            // list, or "Изменить в панели слева") opens that selection, not the creation tab.
            _contextPrefersToolPanel = !RiverRoadHasSelection();
        }

        if (_lastContextSelection != selectionSignature)
        {
            _lastContextSelection = selectionSignature;
            if (selectionSignature is not null)
            {
                _contextPrefersToolPanel = false;
            }
        }

        var scope = EditorContextPanel.Resolve(
            _editor.ActiveTool,
            _editor.SelectionCount,
            _editor.SelectionKind is not null,
            _contextPrefersToolPanel);
        if (_editor.ActiveTool == EditorTool.Stamp && (_editor.SelectedEditablePath is not null || _editor.StampPlacementMode == StampPlacementMode.FillArea))
            scope = EditorContextPanelKind.ToolPanel;
        // A river or a road is not a scene object: its panel is the only place its properties live.
        if (_editor.ActiveTool is EditorTool.River or EditorTool.Road)
            scope = EditorContextPanelKind.ToolPanel;

        var surfaceTool = _editor.ActiveTool is EditorTool.River or EditorTool.Road;
        // The Select tool has no settings of its own, so its "Инструмент" tab would be empty.
        _contextSwitch.IsVisible = !_contextCollapsed && _editor.ActiveTool != EditorTool.Select
            && (surfaceTool ? RiverRoadHasSelection() : EditorContextPanel.ToggleApplies(_editor.SelectionCount));
        var objectTab = surfaceTool ? RiverRoadObjectScope() : scope is not EditorContextPanelKind.ToolPanel;
        Ui.SetSelected(_contextSwitchButtons[0], objectTab);
        Ui.SetSelected(_contextSwitchButtons[1], !objectTab);
        SyncSurfaceScope();
        ToolTip.SetTip(_contextSwitchButtons[1], "Настройки: " + EditorLabels.Tool(_editor.ActiveTool));

        if (scope == EditorContextPanelKind.MultiSelectionInspector)
        {
            _contextTitle.Text = "Выбрано: " + EditorLabels.Objects(_editor.SelectionCount);
            _appearanceTransferHost.IsVisible = true;
            SetContextHeader(MapIcons.LineSelect, "", Ui.Scope.Object,
                EditorLabels.Objects(_editor.SelectionCount) + SelectionLayerSuffix(), null);
            SyncMultiSelectionPanel();
            _contextHost.Content = _multiSelectionRoot;
            return;
        }

        if (scope == EditorContextPanelKind.ObjectInspector && _editor.SelectionKind is { } kind)
        {
            _contextTitle.Text = _editor.Session.SelectedObject?.Name ?? EditorLabels.ObjectKind(kind);
            _appearanceTransferHost.IsVisible = true;
            SetContextHeader(ObjectKindIcon(kind), "", Ui.Scope.Object,
                EditorLabels.ObjectKind(kind).ToLowerInvariant() + SelectionLayerSuffix(), null);

            if (_editor.Session.SelectedObject is { } selected)
            {
                Ui.SetValue(_objectVisibilityValue, "Видимость",
                    EditorLabels.ObjectVisibilityValue(selected.Visible));
                Ui.SetValue(_objectLockValue, "Блокировка",
                    EditorLabels.ObjectLockValue(selected.Locked));
            }

            SyncLayerAndLevel();
            SyncAreaLinks();

            _kindSlot.Content = KindSection(kind);
            _contextHost.Content = _selectionRoot;
            return;
        }

        _contextTitle.Text = _editor.ActiveTool != EditorTool.Stamp ? EditorLabels.Tool(_editor.ActiveTool)
            : _editor.SelectedEditableArea is not null ? "Выбранная область"
            : _editor.SelectedEditablePath is not null ? "Выбранный путь" : "Ассеты";
        SyncToolHeader();
        // Copying a look needs something selected to copy from or to: the river and road panels
        // edit a selected surface, every other tool panel has no target, so the footer hides.
        _appearanceTransferHost.IsVisible = RiverRoadObjectScope();
        _kindSlot.Content = null;
        var surfaceSelected = _editor.SelectionCount == 0
            && (_editor.WaterSelectionCount > 0 || _editor.RoadSelectionCount > 0 || _editor.SelectedLand is not null);
        _selectionEmpty.IsVisible = scope == EditorContextPanelKind.SelectIdle && !surfaceSelected;
        _surfaceSelectedState.IsVisible = scope == EditorContextPanelKind.SelectIdle && surfaceSelected;
        if (_surfaceSelectedState.IsVisible) SyncSurfaceSelectedState();
        _toolSlot.Content = _toolPanels[_editor.ActiveTool];
        _contextHost.Content = _selectIdleRoot;
    }

    /// <summary>
    /// The header of the context panel and the scope line under it: which tool or object the
    /// panel is about, its key, and in words whom the settings below act on.
    /// </summary>
    private void SetContextHeader(string icon, string key, Ui.Scope? scope, string target, Control? action)
    {
        _contextIcon.Content = MapIcons.Grid24(icon, 19,
            Application.Current?.FindResource(scope == Ui.Scope.Object ? "MapSuccessBrush" : "MapAccentBrush") as IBrush);
        if (_contextKey.Child is TextBlock keyText) keyText.Text = key;
        _contextKey.IsVisible = key.Length > 0;
        _contextScopeChip.Content = scope is { } kind ? Ui.ScopeChip(kind) : null;
        _contextScopeTarget.Text = target;
        ToolTip.SetTip(_contextScopeTarget, target);
        _contextScopeLine.IsVisible = !_contextCollapsed && scope is not null;
        _contextHeaderAction.Content = action;
    }

    private string SelectionLayerSuffix() =>
        _editor.ObjectLayerTargets.FirstOrDefault(target => target.IsCurrent) is { } layer
            ? " · слой «" + layer.Name + "»" : "";

    private string ActiveLayerTarget(string prefix) =>
        _editor.ActiveLayer is { } layer ? prefix + " «" + layer.Name + "»" : "слой не выбран";

    private static string ObjectKindIcon(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Stamp or SceneObjectKind.Placeholder => MapIcons.LineAssets,
        SceneObjectKind.Line => MapIcons.LineStroke,
        SceneObjectKind.Text => MapIcons.LineText,
        SceneObjectKind.Decoration => MapIcons.HexRuler,
        _ => MapIcons.LineShape,
    };

    /// <summary>Header and scope for a tool panel, or for the area/path/river/road it edits.</summary>
    private void SyncToolHeader()
    {
        var tool = _editor.ActiveTool;
        var icon = MapIcons.LineTool(tool);
        var key = ToolShortcut(tool);
        if (tool == EditorTool.Stamp && _editor.SelectedEditableArea is not null)
        {
            // The asset panel keeps its reset in every state it had it before, area included.
            SetContextHeader(MapIcons.ModeArea, "", Ui.Scope.Object, "область размещения", _stampResetButton);
            SyncStampResetButton();
            return;
        }
        if (tool == EditorTool.Stamp && _editor.SelectedEditablePath is not null)
        {
            SetContextHeader(MapIcons.ModePath, "", Ui.Scope.Object, "путь размещения", _stampResetButton);
            SyncStampResetButton();
            return;
        }
        switch (tool)
        {
            case EditorTool.Stamp:
                SetContextHeader(icon, key, Ui.Scope.New, ActiveLayerTarget("на слой"), _stampResetButton);
                SyncStampResetButton();
                break;
            case EditorTool.River or EditorTool.Road:
                var editing = RiverRoadObjectScope();
                SetContextHeader(icon, key, editing ? Ui.Scope.Object : Ui.Scope.New,
                    editing ? (tool == EditorTool.River ? "выбранная вода" : "выбранная дорога") + SurfaceLayerSuffix()
                        : ActiveLayerTarget(tool == EditorTool.River ? "новые реки на слой" : "новые дороги на слой"), null);
                break;
            case EditorTool.Land or EditorTool.Brush or EditorTool.Mask:
                SetContextHeader(icon, key, Ui.Scope.Layer, ActiveLayerTarget("рисует на"), null);
                break;
            case EditorTool.Line or EditorTool.Shape or EditorTool.Text:
                SetContextHeader(icon, key, Ui.Scope.New, ActiveLayerTarget("на слой"), null);
                break;
            default:
                SetContextHeader(icon, key, null, "", null);
                break;
        }
    }

    private string SurfaceLayerSuffix() =>
        _editor.ObjectLayerTargets.FirstOrDefault(target => target.IsCurrent) is { } layer
            ? " · слой «" + layer.Name + "»" : "";

    /// <summary>
    /// Keeps the multi-selection panel honest: the count, the group identity when there is one,
    /// which group actions currently apply, and mixed values reported as mixed.
    /// </summary>
    /// <summary>The short count a one-line layer row shows at its right edge.</summary>
    private static string LayerMeta(MapLayer layer) => layer switch
    {
        ObjectLayer objects when objects.Rivers.Length > 0 || objects.Roads.Length > 0 =>
            string.Join(" · ", new[]
            {
                objects.Objects.Count > 0 ? objects.Objects.Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : null,
                objects.Rivers.Length > 0 ? objects.Rivers.Length + " вод." : null,
                objects.Roads.Length > 0 ? objects.Roads.Length + " дор." : null,
            }.Where(part => part is not null)),
        ObjectLayer objects => objects.Objects.Count == 0 ? "пусто"
            : objects.Objects.Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")),
        LandLayer => "земля",
        BaseColorLayer => "фон",
        GridLayer => "сетка",
        _ => "",
    };

    private Control _surfaceSelectedState = null!;
    private TextBlock _surfaceSelectedText = null!;
    private Button _surfaceSelectedOpen = null!;

    /// <summary>
    /// With the Select tool, a river, road or piece of land can be selected, but its properties
    /// live in its own tool. The panel says so and opens that tool instead of claiming that
    /// nothing is selected while the strip above the map names the selection.
    /// </summary>
    private Control BuildSurfaceSelectedState()
    {
        _surfaceSelectedText = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("role-body");
        _surfaceSelectedOpen = Ui.CommandButton("OpenSurfaceToolButton", "Изменить", MapIcons.River, () =>
        {
            _editor.ActiveTool = _editor.WaterSelectionCount > 0 ? EditorTool.River
                : _editor.RoadSelectionCount > 0 ? EditorTool.Road : EditorTool.Land;
            SyncContextArea();
        }, "accent", "compact");
        _surfaceSelectedOpen.HorizontalAlignment = HorizontalAlignment.Stretch;
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                _surfaceSelectedText,
                Ui.Hint("Слой, уровень и порядок — в панели над картой."),
                _surfaceSelectedOpen,
            },
        };
    }

    private void SyncSurfaceSelectedState()
    {
        var (what, tool) = _editor.WaterSelectionCount > 0 ? ("Выбрана вода", "«Река»")
            : _editor.RoadSelectionCount > 0 ? ("Выбрана дорога", "«Дорога»") : ("Выбрана земля", "«Земля»");
        var name = _editor.WaterSelectionCount > 0 ? _editor.SelectedRiver?.Name
            : _editor.RoadSelectionCount > 0 ? _editor.SelectedRoad?.Name : _editor.SelectedLand?.Name;
        _surfaceSelectedText.Text = $"{what} «{name}». Форма и оформление меняются в инструменте {tool}.";
        Ui.SetCommandText(_surfaceSelectedOpen, "Изменить в инструменте " + tool);
    }

    private void SyncMultiSelectionPanel()
    {
        _multiCountText.Text = _editor.SelectedGroupId is not null
            ? $"Группа «{_editor.SelectedGroupName ?? "Группа"}» · {EditorLabels.Objects(_editor.SelectionCount)}"
            : $"Выбрано: {EditorLabels.Objects(_editor.SelectionCount)}";
        AutomationProperties.SetName(_multiCountText, _multiCountText.Text);

        var hasGroup = _editor.SelectedGroupId is not null;
        var canGroup = _editor.CanGroupSelection;

        // When neither creating nor managing a group applies, the whole section disappears -
        // a leftover "Группа" heading over nothing would read as a broken panel.
        _groupActionsSection.IsVisible = hasGroup || canGroup;
        _groupNameSection.IsVisible = hasGroup || canGroup;
        _groupNameBox.IsVisible = hasGroup || canGroup;
        _groupCreateButton.IsVisible = canGroup;
        _groupRenameButton.IsVisible = hasGroup;
        _enterGroupButton.IsVisible = hasGroup;
        _ungroupButton.IsVisible = hasGroup;
        _groupNameBox.PlaceholderText = hasGroup
            ? _editor.SelectedGroupName ?? "Имя группы"
            : "Имя группы";

        Ui.SetValue(_multiVisibilityValue, "Видимость", _editor.SelectionVisibilitySummary);
        Ui.SetValue(_multiLockValue, "Блокировка", _editor.SelectionLockSummary);
        SyncLayerAndLevel();
        SyncAreaLinks();
    }

    private readonly List<Button> _areaLinks = [];

    /// <summary>
    /// DEC-070: selected objects of a placement area lead straight to that area's «Красота» (the owner could not find it
    /// under the asset tool). Shown only when every selected object belongs to one area.
    /// </summary>
    private Button AreaLinkButton(string name)
    {
        var button = Ui.TextButton(name, "Область", () => OpenSelectionAreaTab("EditableAreaTabsBeauty"), "compact", "primary");
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 0, 0, 8);
        button.IsVisible = false;
        button.WithTip("Открыть область этих объектов: плотность, правила и «Красота».");
        _areaLinks.Add(button);
        return button;
    }

    private void OpenSelectionAreaTab(string tabName)
    {
        if (!_editor.OpenSelectionArea()) return;
        // The asset tool's panel is mounted by the sync that follows; select the requested existing page afterwards.
        Dispatcher.UIThread.Post(() =>
        {
            var tab = this.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .FirstOrDefault(b => b.Name == tabName);
            tab?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            tab?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void SyncAreaLinks()
    {
        var area = _editor.SelectionArea;
        foreach (var link in _areaLinks)
        {
            link.IsVisible = area is not null;
            if (area is not null) link.Content = $"Область «{area.Name}» → Красота";
        }
    }

    /// <summary>Both inspector shapes show the same owner layer and level, so both refresh here.</summary>
    private void SyncLayerAndLevel()
    {
        foreach (var sync in _levelSyncs)
        {
            sync();
        }
    }

    /// <summary>
    /// The only place that decides which kind-specific block is shown. Exactly one block can be
    /// mounted at a time because they all share a single slot.
    /// </summary>
    private Control? KindSection(SceneObjectKind kind) =>
        EditorSurfaceLayout.SectionFor(kind) switch
        {
            ObjectKindSection.Asset => _stampSection,
            ObjectKindSection.Line => _lineSection,
            ObjectKindSection.Shape => _shapeSection,
            ObjectKindSection.Primitive => _primitiveSection,
            ObjectKindSection.Text => _textSection,
            ObjectKindSection.Composition => _compositionSection,
            ObjectKindSection.Decoration => _decorationSection,
            _ => null,
        };

    private void BuildContextRoots()
    {
        _kindSlot = new RetainedSlot { Name = "SelectionKindSlot" };

        // Order is the message: what the object IS and the properties touched constantly come
        // first, the kind-specific block next, and exact geometry last. The old panel opened with
        // four coordinate boxes, so the level and the owning layer were below the fold.
        var geometry = new Ui.SectionPanel("ObjectGeometrySection", "Положение и размер", _inspectorPanel, false);
        geometry.SetSummary("X, Y, ширина, высота");
        _selectionRoot = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                AreaLinkButton("SelectionAreaBeautyButton"),
                BuildSelectionCommon(),
                new Border { Padding = new Thickness(0, 8, 0, 0), Child = _kindSlot },
                geometry,
                new Border
                {
                    Padding = new Thickness(0, 10, 0, 4),
                    Child = Ui.Hint("Слой, уровень, порядок, размер, масштаб, непрозрачность и поворот — в панели над картой."),
                },
                _objectDeleteButton,
            },
        };

        // The tool panels live in a slot, never as fixed children: a control can have only one
        // visual parent, and the context area now shows a tool panel in more situations than the
        // old "Select tool only" rule allowed.
        _toolSlot = new RetainedSlot { Name = "ToolPanelSlot" };
        _surfaceSelectedState = BuildSurfaceSelectedState();
        _selectIdleRoot = new StackPanel
        {
            Spacing = 12,
            Children = { _selectionEmpty, _surfaceSelectedState, _toolSlot },
        };

        _multiSelectionRoot = BuildMultiSelectionPanel();
    }

    /// <summary>
    /// What N selected objects show instead of the single inspector: an honest count, the group
    /// actions when they apply, and the common visibility/lock/delete over the whole set. One
    /// member's numbers are never presented as if they were shared.
    /// </summary>
    private StackPanel BuildMultiSelectionPanel()
    {
        var multiCount = new TextBlock { Name = "MultiSelectionCountText", TextWrapping = TextWrapping.Wrap }
            .Classed("role-panel");
        _multiCountText = multiCount;

        AutomationProperties.SetName(_groupNameBox, "Имя группы");
        _groupNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                UiTasks.Forget(this, GroupSelectionInteractiveAsync);
                e.Handled = true;
            }
        };

        _groupCreateButton = ShortcutTextButton(
            "GroupCreateButton", "Сгруппировать", EditorShortcutSettings.Commands.Group,
            () => UiTasks.Forget(this, GroupSelectionInteractiveAsync), "primary", "compact");
        _groupRenameButton = Ui.TextButton("GroupRenameButton", "Переименовать группу", () =>
        {
            if (_editor.RenameSelectedGroup(_groupNameBox.Text ?? ""))
            {
                _groupNameBox.Text = "";
            }

            RefreshAfterSelectionCommand();
        }, "compact");
        _ungroupButton = ShortcutTextButton(
            "UngroupButton", "Разгруппировать", EditorShortcutSettings.Commands.Ungroup, () =>
        {
            _editor.UngroupSelection();
            RefreshAfterSelectionCommand();
        }, "compact");
        _enterGroupButton = Ui.TextButton("EnterGroupButton", "Открыть группу", () =>
        {
            _editor.EnterSelectedGroup();
            RefreshAfterSelectionCommand();
        }, "compact");

        _groupNameSection = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                _groupNameBox,
                _groupCreateButton,
                _groupRenameButton,
                _enterGroupButton,
                _ungroupButton,
            },
        };

        var (visibilityRow, visibilityButton) = PropertyValueRow(
            "MultiVisibilityButton", "Видимость", "-",
            [("Показан", () => _editor.SetSelectedObjectVisibility(true)),
             ("Скрыт", () => _editor.SetSelectedObjectVisibility(false))]);
        _multiVisibilityValue = visibilityButton;
        var (lockRow, lockButton) = PropertyValueRow(
            "MultiLockButton", "Блокировка", "-",
            [("Свободен", () => _editor.SetSelectedObjectLock(false)),
             ("Заблокирован", () => _editor.SetSelectedObjectLock(true))]);
        _multiLockValue = lockButton;

        var delete = Ui.CommandButton("MultiDeleteButton", "Удалить выделение", MapIcons.Trash,
            _editor.DeleteSelectedObject, "danger-text");
        delete.Margin = new Thickness(0, 8, 0, 0);
        DynamicTip(delete, () =>
            $"Удалить все выбранные объекты. Действие можно отменить ({_editor.Shortcuts.Get(EditorShortcutSettings.Commands.Undo)}).");

        _groupActionsSection = Ui.Section("Группа", _groupNameSection).Named("GroupActionsSection");
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                multiCount,
                AreaLinkButton("MultiSelectionAreaBeautyButton"),
                BuildObjectPrimarySection("Multi"),
                _groupActionsSection,
                new Ui.SectionPanel("MultiCommonActions", "Состояние", new StackPanel
                {
                    Spacing = 2,
                    Children = { visibilityRow, lockRow },
                }, true),
                Ui.Hint("Слой, уровень, порядок, масштаб, непрозрачность и поворот — в панели над картой."),
                delete,
            },
        };
    }

    private Control _groupActionsSection = null!;
    private Button _objectDeleteButton = null!;

    private TextBlock _multiCountText = null!;

    private void RefreshAfterSelectionCommand()
    {
        SyncStatus();
        SyncSidePanels();
        SyncContextArea();
    }

    /// <summary>
    /// The right-click menu of the canvas: what lies under the pointer becomes the selection,
    /// and the menu offers the moves that decide what covers what - order inside the level,
    /// the level itself, the owning layer - plus deletion. Every item calls the same view model
    /// command the strip and the shortcuts call, so the three can never disagree. Empty ground
    /// under the pointer opens nothing.
    /// </summary>
    private void ShowCanvasContextMenu(PointerPressedEventArgs e)
    {
        var position = e.GetPosition(_canvas);
        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);
        var hit = SceneGeometry.HitTest(_editor.Session.Document, worldX, worldY);
        if (hit is not null)
        {
            if (!_editor.Session.SelectedObjects.Any(item => item.Id == hit.Id))
            {
                _editor.SelectObject(hit.Id);
            }
        }
        else if (!_editor.SelectSurfaceAt(worldX, worldY, _editor.Session.Viewport.Scale, additive: false)
            && !_editor.SelectLandAt(worldX, worldY))
        {
            return;
        }
        RefreshAfterSelectionCommand();

        // Objects, water, roads and land answer the same verbs; the view model routes each to
        // its own command. Land has no order of its own besides its level and its layer.
        var objects = _editor.SelectionCount > 0;
        var water = !objects && _editor.SurfaceSelectionCount > 0;
        var landPiece = !objects && !water && _editor.SelectedLand is not null;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Выше", EditorShortcutSettings.Commands.ObjectUp, () => _editor.ReorderSelection(1), objects || water));
        menu.Items.Add(Item("Ниже", EditorShortcutSettings.Commands.ObjectDown, () => _editor.ReorderSelection(-1), objects || water));
        menu.Items.Add(Item("Наверх", EditorShortcutSettings.Commands.ObjectTop, () => _editor.ReorderSelection(1, toExtreme: true), objects || water));
        menu.Items.Add(Item("Вниз", EditorShortcutSettings.Commands.ObjectBottom, () => _editor.ReorderSelection(-1, toExtreme: true), objects || water));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Уровень выше", EditorShortcutSettings.Commands.SublayerUp, () => _editor.AdjustSelectedSublayer(1), true));
        menu.Items.Add(Item("Уровень ниже", EditorShortcutSettings.Commands.SublayerDown, () => _editor.AdjustSelectedSublayer(-1), true));
        if (landPiece)
        {
            menu.Items.Add(Item("Слой земли выше", EditorShortcutSettings.Commands.LayerUp, () => _editor.ReorderActiveLayer(true), true));
            menu.Items.Add(Item("Слой земли ниже", EditorShortcutSettings.Commands.LayerDown, () => _editor.ReorderActiveLayer(false), true));
        }
        menu.Items.Add(new Separator());

        var layers = new MenuItem { Name = "CanvasMenuLayer", Header = "Перенести на слой" };
        foreach (var target in _editor.ObjectLayerTargets)
        {
            var captured = target;
            var item = new MenuItem
            {
                Name = "CanvasMenuLayer_" + target.Id,
                Header = target.IsCurrent ? "● " + target.Name : target.Display,
                IsEnabled = target.Available && !target.IsCurrent,
            };
            item.Click += (_, _) =>
            {
                _editor.MoveSelectionToLayer(captured.Id);
                RefreshAfterSelectionCommand();
            };
            layers.Items.Add(item);
        }
        layers.IsEnabled = layers.Items.Count > 0 && !landPiece;
        menu.Items.Add(layers);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Удалить", EditorShortcutSettings.Commands.Delete, () => _editor.DeleteSelection(), !landPiece));
        menu.ShowAt(_canvas, showAtPointer: true);
        e.Handled = true;

        MenuItem Item(string header, string command, Action action, bool enabled)
        {
            var item = new MenuItem { Name = "CanvasMenu_" + command, Header = header, IsEnabled = enabled };
            var gesture = _editor.Shortcuts.Get(command);
            try { item.InputGesture = KeyGesture.Parse(gesture); }
            catch (Exception) { item.Header = header + "   " + gesture; }
            item.Click += (_, _) =>
            {
                action();
                RefreshAfterSelectionCommand();
            };
            return item;
        }
    }

    /// <summary>
    /// The interactive Ctrl+G flow: the panel's name box wins when filled; otherwise a small
    /// dialog asks for the name and states exactly how many objects the group will hold.
    /// </summary>
    public System.Threading.Tasks.Task GroupSelectionInteractiveAsync()
    {
        if (!_editor.CanGroupSelection)
        {
            // Produces the honest refusal message (too few objects / already grouped members).
            _editor.GroupSelection("группа");
            RefreshAfterSelectionCommand();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        var name = (_groupNameBox.Text ?? "").Trim();
        if (_editor.GroupSelection(name))
        {
            _groupNameBox.Text = "";
        }

        RefreshAfterSelectionCommand();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void SyncLandButtons()
    {
        SyncLandMaterialPanel();
        for (var index = 0; index < _landActionButtons.Count; index++)
        {
            var active = (index == 1) == _editor.LandSubtract;
            Ui.SetClass(_landActionButtons[index], "active", active);
            if (_landActionButtons[index] is ToggleButton toggle) toggle.IsChecked = active;
        }

        // The shape row never showed a selection at all, so the user could not tell which of the
        // six was in effect - or that clicking one had done anything.
        foreach (var (name, button) in _landShapeButtons)
        {
            var key = name.StartsWith("Land", StringComparison.Ordinal) ? name[4..] : name;
            Ui.SetClass(button, "active", LandShapeByKey(key) == _editor.ActiveLandShape);
        }
    }

    private void SyncMaskModeButtons()
    {
        for (var index = 0; index < _maskModeButtons.Count; index++)
        {
            var active = (index == 1) == (_editor.MaskMode == MaskPaintMode.Reveal);
            Ui.SetClass(_maskModeButtons[index], "active", active);
            if (_maskModeButtons[index] is ToggleButton toggle) toggle.IsChecked = active;
        }
    }

    /// <summary>
    /// Copies the active layer into its property sheet. Every value control is refreshed with
    /// the value it currently holds, so the panel can never claim a setting it does not have.
    /// </summary>
    private void SyncLayerBlocks()
    {
        var layer = _editor.ActiveLayer;
        var editable = layer is not null and not BaseColorLayer;

        _noLayerState.IsVisible = layer is null;
        // The selected row of the list already names the layer; the card would repeat it.
        _layerIdentity.IsVisible = false;
        _layerCommonBlock.IsVisible = editable;
        _baseColorLayerBlock.IsVisible = layer is BaseColorLayer;
        _landStylePointer.IsVisible = layer is LandLayer;
        // In the tool panel the same block appears only while a land layer is the active one;
        // otherwise the panel says which layer to pick instead of showing dead controls.
        var landActive = _editor.ActiveLayer is LandLayer;
        _landLayerBlock.IsVisible = landActive;
        _landStyleMissing.IsVisible = !landActive;
        _gridLayerBlock.IsVisible = layer is GridLayer;
        _filterLayerBlock.IsVisible = layer is FilterLayer;

        if (layer is null)
        {
            return;
        }

        _layerNameHeading.Text = layer.Name;
        ToolTip.SetTip(_layerNameHeading, layer.Name);
        _layerKindIcon.Content = MapIcons.Stroked(
            LayerIcon(layer), 16, Application.Current?.FindResource("MapAccentBrush") as IBrush);

        // Say something the heading does not already say. A layer named "Объекты" of kind
        // "Объекты" gains nothing from a second line repeating the word.
        var kind = EditorLabels.LayerKind(layer);
        var subtitle = layer is BaseColorLayer
            ? "Фон карты · нельзя удалить"
            : EditorLabels.SecondaryKind(layer.Name, kind) is null
                ? $"Наложение: {EditorLabels.Blend(layer.Blend)}"
                : kind;
        _layerKindText.Text = subtitle;

        if (!editable)
        {
            return;
        }

        Ui.SetValue(_visibilityValue, "Видимость", layer.Visible ? "Показан" : "Скрыт");
        Ui.SetValue(_lockValue, "Блокировка", layer.Locked ? "Заблокирован" : "Свободен");
        Ui.SetValue(_maskValue, "Маска", EditorLabels.MaskState(layer));

        if (layer is LandLayer land)
        {
            Ui.SetValue(_landEdgeValue, "Берег", EditorLabels.LandEdge(land));
        }

        if (layer is GridLayer grid)
        {
            _gridLayerBlock.IsEnabled = !grid.Locked;
        }

        if (layer is FilterLayer filter)
        {
            Ui.SetValue(_filterPresetValue, "Пресет", EditorLabels.FilterPreset(filter));
        }
    }

    /// <summary>
    /// The rows of the layer list by layer, with the signature of what each row shows. A row is
    /// rebuilt only when that changes: a stroke, a placement or an undo changes the tiles or the
    /// objects of a layer, not its row (its object count aside), so the list is not torn down
    /// and rebuilt control by control on every edit.
    /// </summary>
    private readonly Dictionary<LayerId, (string Signature, Border Row)> _layerRows = [];

    private void SyncSidePanels()
    {
        var sidePanelsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = new List<Border>(_editor.Layers.Count);
        var seen = new HashSet<LayerId>();
        foreach (var layer in _editor.Layers.Reverse())
        {
            seen.Add(layer.Id);
            var signature = LayerRowSignature(layer);
            if (_layerRows.TryGetValue(layer.Id, out var kept) && kept.Signature == signature)
            {
                Ui.SetStructureSelected(kept.Row, layer.Id == _editor.ActiveLayerId);
                rows.Add(kept.Row);
                continue;
            }
            var built = BuildLayerRow(layer);
            _layerRows[layer.Id] = (signature, built);
            rows.Add(built);
        }
        foreach (var gone in _layerRows.Keys.Where(id => !seen.Contains(id)).ToArray()) _layerRows.Remove(gone);
        if (_layerList.Children.Count == rows.Count)
        {
            // Same layers in the same order: only the rows that were rebuilt are swapped, so the
            // others keep their visual state and are not detached and re-styled.
            for (var i = 0; i < rows.Count; i++)
                if (!ReferenceEquals(_layerList.Children[i], rows[i])) _layerList.Children[i] = rows[i];
        }
        else
        {
            _layerList.Children.Clear();
            foreach (var row in rows) _layerList.Children.Add(row);
        }

        Border BuildLayerRow(MapLayer layer)
        {
            var captured = layer;
            var isBase = layer is BaseColorLayer;

            // The base colour layer cannot be hidden, locked or removed, so it gets no toggles
            // rather than disabled ones that would only invite a pointless click.
            var actions = new List<Button>();
            if (!isBase)
            {
                // A layer switch acts on everything the layer holds. Where that is more than
                // objects, the label says so, since the sections offer a switch per river or road.
                var scope = layer is ObjectLayer { Rivers.Length: > 0 } or ObjectLayer { Roads.Length: > 0 }
                    ? " целиком: объекты, вода и дороги" : "";
                var visibility = Ui.RowAction(
                    "LayerRowVisibility_" + layer.Id,
                    EditorLabels.VisibilityCommand(layer.Visible) + scope,
                    layer.Visible ? MapIcons.Eye : MapIcons.EyeOff,
                    () => _editor.ToggleLayerVisibility(captured.Id));
                Ui.SetRowAction(visibility, EditorLabels.VisibilityCommand(layer.Visible) + scope, !layer.Visible);

                var locking = Ui.RowAction(
                    "LayerRowLock_" + layer.Id,
                    EditorLabels.LockCommand(layer.Locked) + scope,
                    layer.Locked ? MapIcons.Lock : MapIcons.Unlock,
                    () => _editor.ToggleLayerLock(captured.Id));
                Ui.SetRowAction(locking, EditorLabels.LockCommand(layer.Locked) + scope, layer.Locked);

                actions.Add(visibility);
                actions.Add(locking);
            }

            // The kind is a useful subtitle only when it adds something the name does not say.
            // An object layer also says what it holds, so the stack can be read without opening
            // each layer: how many objects, how many rivers and lakes.
            var kind = EditorLabels.LayerKind(layer);
            var secondary = EditorLabels.SecondaryKind(layer.Name, kind);
            if (layer is ObjectLayer objectLayer)
            {
                var counts = new List<string>();
                if (objectLayer.Objects.Count > 0) counts.Add(EditorLabels.Objects(objectLayer.Objects.Count));
                if (objectLayer.Rivers.Length > 0) counts.Add(EditorLabels.Waters(objectLayer.Rivers.Length));
                if (objectLayer.Roads.Length > 0) counts.Add(EditorLabels.RoadsCount(objectLayer.Roads.Length));
                var held = counts.Count == 0 ? "пусто" : string.Join(" · ", counts);
                secondary = secondary is null ? held : secondary + " · " + held;
            }

            var tooltip = $"{layer.Name} · {kind} · наложение: {EditorLabels.Blend(layer.Blend)}";
            if (layer.Mask is { } mask)
            {
                tooltip += mask.Enabled ? " · маска включена" : " · маска выключена";
            }

            var row = Ui.StructureRow(
                "Layer_" + layer.Id,
                LayerIcon(layer),
                layer.Name,
                null,
                () =>
                {
                    _editor.SelectLayer(captured.Id);
                    _layerName.Text = captured.Name;
                    SyncSidePanels();
                    SyncContextArea();
                },
                tooltip + (secondary is null ? "" : " · " + secondary),
                [.. actions]);
            Ui.SetStructureMeta(row, LayerMeta(layer));

            Ui.SetStructureSelected(row, layer.Id == _editor.ActiveLayerId);
            return row;
        }

        _layerName.Text = _editor.ActiveLayer?.Name ?? "";
        var timings = EditorViewModel.CollectEditTimings ? _editor.LastEditTimings : null;
        void Timed(string step, Action action)
        {
            if (timings is null) { action(); return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            timings.Add(("ui:side/" + step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        if (timings is not null) timings.Add(("ui:side/layer-rows", System.Diagnostics.Stopwatch.GetElapsedTime(sidePanelsStarted).TotalMilliseconds));
        Timed("layer-blocks", SyncLayerBlocks);
        Timed("object-list", SyncObjectList);
        Timed("asset-list", SyncAssetList);
        Timed("note-list", SyncNoteList);
        Timed("entity-list", SyncEntities);
        Timed("fog", SyncFog);
        Timed("compare", SyncCompareBlock);
        Timed("transitions", SyncTransitions);
        Timed("timeline", SyncTimeline);
        Timed("pointcrawl", SyncPointcrawl);
        Timed("planet", SyncPlanet);
        Timed("book", SyncBook);
        Timed("sector", SyncSector);
        Timed("checkpoint-list", SyncCheckpointList);
        Timed("canvas-workflow", SyncCanvasWorkflow);
        Timed("quick-bar", SyncQuickBar);
    }

    /// <summary>Everything a layer row shows, as one string: name, kind, switches, counts, blend and mask state.</summary>
    private static string LayerRowSignature(MapLayer layer)
    {
        var counts = layer is ObjectLayer objects ? $"{objects.Objects.Count}/{objects.Rivers.Length}/{objects.Roads.Length}" : "";
        var mask = layer.Mask is { } m ? (m.Enabled ? "m+" : "m-") : "m0";
        return $"{layer.GetType().Name}|{layer.Name}|{layer.Visible}|{layer.Locked}|{layer.Blend}|{mask}|{counts}|{LayerMeta(layer)}";
    }

    private static string LayerIcon(MapLayer layer) => layer switch
    {
        BaseColorLayer => MapIcons.LayerBase,
        ObjectLayer => MapIcons.LayerObjects,
        BrushLayer => MapIcons.LayerBrush,
        LandLayer => MapIcons.LayerLand,
        GridLayer => MapIcons.LayerGrid,
        FilterLayer => MapIcons.LayerFilter,
        _ => MapIcons.Layers,
    };

    private void SyncObjectList()
    {
        _objectListRebuildCount++;
        RefreshObjectSelectionSet();
        SyncObjectFilterButtons();
        _selectionCountText.Text = (_editor.SelectionCount, _editor.WaterSelectionCount, _editor.RoadSelectionCount) switch
        {
            (0, 0, 0) when _editor.SelectedLand is { } land => $"Выбрана земля «{land.Name}»",
            (0, 0, 0) => "Ничего не выбрано",
            (0, var water, 0) => $"Выбрано водных систем: {water}",
            (0, 0, var roads) => $"Выбрано дорог: {roads}",
            (0, var water, var roads) => $"Выбрано водных систем: {water} и дорог: {roads}",
            (var objects, 0, 0) => $"Выбрано: {EditorLabels.Objects(objects)}",
            var (objects, water, roads) => $"Выбрано: {EditorLabels.Objects(objects)}, водных систем: {water}, дорог: {roads}",
        };
        var anySelected = _editor.SelectionCount>0 || _editor.WaterSelectionCount>0
            || _editor.RoadSelectionCount>0 || _editor.SelectedLand is not null;
        // "Выбрано водных систем: 1" under the Assets list only confused: the count is shown in
        // the section that lists the selection.
        _selectionCountText.IsVisible = anySelected && _editor.SectionForSelection() is { } home && home == _editor.PanelSection;

        SyncSectionFooter();
        var rows = _editor.ObjectTree;
        if (rows.Count == 0)
        {
            var searching = !string.IsNullOrWhiteSpace(_editor.ObjectSearchText)
                || (_editor.PanelSection == PanelSection.Other && _editor.ObjectTypeFilter != ObjectTypeFilter.NotStamps
                    && _editor.Layers.OfType<ObjectLayer>().Any(layer => layer.Objects.Any(o => o.Kind != SceneObjectKind.Stamp)));
            _objectEmptyStateHost.Content = SectionEmptyState(_editor.PanelSection, searching);
            _objectEmptyStateHost.IsVisible = true;
            _objectList.IsVisible = false;
            _objectList.ItemsSource = Array.Empty<ObjectTreeRow>();
            return;
        }

        _objectEmptyStateHost.IsVisible = false;
        _objectList.IsVisible = true;
        _objectList.ItemsSource = rows;
        Dispatcher.UIThread.Post(() => { SyncObjectListSelection(); ScrollObjectListToSelection(); }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Every copy of one asset picture as one row: the picture, its name, how many copies the map
    /// holds. A click opens the copies; a row whose copies include the selection is highlighted.
    /// </summary>
    private Button BuildAssetKindRow(ObjectTreeRow row)
    {
        var key = row.AssetKey!;
        var chevron = new Border { Width = 18, Child = MapIcons.Stroked(MapIcons.ChevronRight, 14) };
        var listRow = Ui.ListRow(
            row.AutomationId,
            MapIcons.ObjectStamp,
            row.Title,
            row.Subtitle,
            [],
            () =>
            {
                _editor.OpenAssetKind(key);
                SyncObjectList();
            },
            $"«{row.Title}» · {row.Subtitle} на карте. Открыть список копий.",
            row.Object is { } first ? _thumbnails.Image(_editor.ObjectThumbnailPath(first)) : null,
            chevron);
        listRow.MinHeight = 38;
        Ui.SetSelected(listRow, row.Selected);
        return listRow;
    }

    /// <summary>The way back from an opened asset to the list of assets, naming the open one.</summary>
    private Border BuildAssetKindBreadcrumbRow(ObjectTreeRow row)
    {
        var back = Ui.TextButton("AssetKindBackButton", "← Все ассеты", () =>
        {
            _editor.CloseAssetKind();
            SyncObjectList();
        }, "compact");
        var title = Ui.Trimmed($"{row.Title} · {row.Subtitle}", "muted").Named(row.AutomationId);
        AutomationProperties.SetName(title, $"Открыт ассет «{row.Title}», {row.Subtitle}");
        return new Border
        {
            Child = new DockPanel { Children = { back.Docked(Dock.Left), title } },
            Padding = new Thickness(0, 2),
        };
    }

    /// <summary>
    /// After a selection made elsewhere (on the map), the list scrolls so the selected row is in
    /// view. Only done for a new selection, and only for lists short enough to scan cheaply.
    /// </summary>
    private bool _scrollListToSelection;

    private void ScrollObjectListToSelection()
    {
        if (!_scrollListToSelection || _objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows) return;
        _scrollListToSelection = false;
        if (rows.Count > 20000) return;
        for (var index = 0; index < rows.Count; index++)
        {
            if (!IsObjectTreeRowSelected(rows[index])) continue;
            _objectList.ScrollIntoView(index);
            return;
        }
    }

    /// <summary>The breadcrumb of an open group: the way back plus the group's own name.</summary>
    private Border BuildGroupBreadcrumbRow(ObjectTreeRow row)
    {
        var back = Ui.TextButton("GroupBackButton", "← Все объекты", () =>
        {
            _editor.ExitGroup();
            SyncObjectList();
            SyncContextArea();
        }, "compact");

        // The identity sits on the TextBlock: a bare Border has no automation peer, so naming
        // the host would leave the breadcrumb invisible to UI Automation.
        var title = Ui.Trimmed($"{row.Title} · {row.Subtitle}", "muted").Named(row.AutomationId);
        AutomationProperties.SetName(title, $"Открыта группа «{row.Title}», {row.Subtitle}");
        return new Border
        {
            Child = new DockPanel
            {
                Children = { back.Docked(Dock.Left), title },
            },
            Padding = new Thickness(0, 2),
        };
    }

    private Border BuildGroupRow(ObjectTreeRow row)
    {
        var groupId = row.GroupId!.Value;
        var enter = Ui.RowAction(
            $"GroupRowEnter_{groupId:D}", "Открыть группу", MapIcons.ChevronRight,
            () =>
            {
                _editor.EnterGroup(groupId);
                SyncObjectList();
                SyncContextArea();
            });

        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.Objects,
            row.Title,
            row.Subtitle,
            () =>
            {
                _editor.SelectGroup(groupId);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"Группа «{row.Title}» · {row.Subtitle}",
            enter);

        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    /// <summary>
    /// One river or lake in the objects panel: selecting it is the same selection the canvas uses,
    /// and the two switches beside it hide and lock that water system alone.
    /// </summary>
    private Border BuildWaterRow(ObjectTreeRow row)
    {
        var waterId = row.WaterId!.Value;
        var visible = Ui.RowAction(
            $"WaterRowVisible_{waterId:D}",
            row.WaterVisible ? "Скрыть воду" : "Показать воду",
            row.WaterVisible ? MapIcons.Eye : MapIcons.EyeOff,
            () =>
            {
                _editor.SelectWater(waterId);
                _editor.SetSelectedWaterVisible(!row.WaterVisible);
                SyncObjectList();
                SyncContextArea();
            });
        var locked = Ui.RowAction(
            $"WaterRowLocked_{waterId:D}",
            row.WaterLocked ? "Разблокировать воду" : "Заблокировать воду",
            row.WaterLocked ? MapIcons.Lock : MapIcons.Unlock,
            () =>
            {
                _editor.SelectWater(waterId);
                _editor.SetSelectedWaterLocked(!row.WaterLocked);
                SyncObjectList();
                SyncContextArea();
            });
        var states = row.WaterVisible ? row.WaterLocked ? " · заблокировано" : "" : " · скрыто";
        var owner = row.LayerName is { } layerName ? $" · слой «{layerName}»" : "";
        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.River,
            row.Title,
            row.Subtitle + " · уровень " + EditorViewModel.FormatSublayer(row.Level) + states + owner,
            () =>
            {
                _editor.SelectWater(waterId);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"Вода «{row.Title}» · {row.Subtitle}{states}",
            visible,
            locked);
        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    /// <summary>
    /// One piece of land in the objects panel: selecting it is the same selection a click on
    /// the land makes, and the two switches beside it hide and lock that land layer.
    /// </summary>
    private Border BuildLandRow(ObjectTreeRow row)
    {
        var landId = row.LandId!.Value;
        var visible = Ui.RowAction(
            $"LandRowVisible_{landId}",
            row.WaterVisible ? "Скрыть землю" : "Показать землю",
            row.WaterVisible ? MapIcons.Eye : MapIcons.EyeOff,
            () =>
            {
                _editor.ToggleLayerVisibility(landId);
                SyncObjectList();
                SyncContextArea();
            });
        var locked = Ui.RowAction(
            $"LandRowLocked_{landId}",
            row.WaterLocked ? "Разблокировать землю" : "Заблокировать землю",
            row.WaterLocked ? MapIcons.Lock : MapIcons.Unlock,
            () =>
            {
                _editor.ToggleLayerLock(landId);
                SyncObjectList();
                SyncContextArea();
            });
        var states = row.WaterVisible ? row.WaterLocked ? " · заблокирована" : "" : " · скрыта";
        var host = Ui.StructureRow(
            row.AutomationId,
            MapIcons.LayerLand,
            row.Title,
            "Земля · уровень " + EditorViewModel.FormatSublayer(row.Level) + states,
            () =>
            {
                _editor.SelectLand(landId);
                SyncObjectListSelection();
                SyncContextArea();
                SyncQuickBar();
            },
            $"Земля «{row.Title}» · уровень {EditorViewModel.FormatSublayer(row.Level)}{states}",
            visible,
            locked);
        Ui.SetStructureSelected(host, row.Selected);
        return host;
    }

    private Button BuildObjectRow(ObjectTreeRow row)
    {
        var sceneObject = row.Object!;
        var states = new List<(string, string)>();
        if (!sceneObject.Visible)
        {
            states.Add((MapIcons.EyeOff, "Объект скрыт"));
        }

        if (sceneObject.Locked)
        {
            states.Add((MapIcons.Lock, "Объект заблокирован"));
        }

        if (sceneObject.Kind == SceneObjectKind.Placeholder)
        {
            states.Add((MapIcons.Missing, "Файл ассета не найден"));
        }

        var captured = sceneObject;

        // A placed asset shows the asset itself. The generic kind glyph and the kind word next to
        // the name only ever repeated what the picture already says ("Дерево  Штамп"), so they are
        // kept for objects that have no picture of their own. The sublayer is what the row still
        // needs to say, because it is the object's own level inside the layer.
        var thumbnail = _thumbnails.Image(_editor.ObjectThumbnailPath(sceneObject));
        var secondary = new List<string>();
        if (thumbnail is null && row.Subtitle is { Length: > 0 } kindLabel)
        {
            secondary.Add(kindLabel);
        }

        // The level is the one number the row always carries, at the far right of every row so
        // the column can be read down the list, and with a sign so "+3" and "-3" can never be
        // read as the same thing. Zero is shown too: a blank would make the column uneven.
        var levelText = EditorViewModel.FormatSublayer(sceneObject.Sublayer);
        var level = new Border
        {
            Padding = new Thickness(5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Children =
                {
                    MapIcons.Stroked(MapIcons.Layers, 12, Application.Current?.FindResource("MapMutedBrush") as IBrush),
                    new TextBlock { Text = levelText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                        .Classed("muted"),
                },
            },
        }.Classed("badge").Named("ObjectRowLevel_" + sceneObject.Id);
        ToolTip.SetTip(level, "Уровень объекта: " + levelText + ". Чем выше уровень, тем выше объект на карте.");
        AutomationProperties.SetName(level, "Уровень " + levelText);

        var listRow = Ui.ListRow(
            row.AutomationId,
            ObjectIcon(sceneObject.Kind),
            sceneObject.Name,
            secondary.Count == 0 ? null : string.Join(" · ", secondary),
            states,
            () =>
            {
                _editor.SelectObject(captured.Id, additive: _objectRowCtrl);
                SyncObjectListSelection();
                SyncContextArea();
            },
            $"{sceneObject.Name} · {EditorLabels.ObjectKind(sceneObject.Kind)} · уровень " + levelText,
            thumbnail,
            level);

        Ui.SetSelected(listRow, row.Selected);
        return listRow;
    }

    private void SyncObjectFilterButtons()
    {
        foreach (var pair in _objectFilterButtons)
        {
            var active = string.Equals(pair.Key, _editor.ObjectTypeFilter.ToString(), StringComparison.Ordinal);
            Ui.SetClass(pair.Value, "active", active);
            if (pair.Value is ToggleButton toggle)
            {
                toggle.IsChecked = active;
            }
        }
    }

    private void RefreshObjectSelectionSet()
    {
        _objectSelectionSet.Clear();
        _objectSelectionSet.UnionWith(_editor.Session.SelectedObjectIds);
    }

    private bool IsObjectTreeRowSelected(ObjectTreeRow row) => row.Kind switch
    {
        ObjectTreeRowKind.Object or ObjectTreeRowKind.GroupChild =>
            row.Object is { } sceneObject && _objectSelectionSet.Contains(sceneObject.Id),
        ObjectTreeRowKind.Group =>
            row.MemberIds is { Count: > 0 } members
            && members.All(_objectSelectionSet.Contains),
        ObjectTreeRowKind.Water =>
            row.WaterId is { } waterId && _editor.WaterSelectionIds.Contains(waterId),
        ObjectTreeRowKind.Road =>
            row.RoadId is { } roadId && _editor.RoadSelectionIds.Contains(roadId),
        ObjectTreeRowKind.Land =>
            row.LandId is { } landId && _editor.SelectedLand?.Id == landId,
        ObjectTreeRowKind.AssetKind =>
            row.MemberIds is { Count: > 0 } kindMembers && kindMembers.Any(_objectSelectionSet.Contains),
        _ => false,
    };

    /// <summary>
    /// Selection-only notifications touch only realized containers. The ItemsSource and its
    /// tens of thousands of logical rows stay intact.
    /// </summary>
    private void SyncObjectListSelection()
    {
        RefreshObjectSelectionSet();
        if (_objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows)
        {
            return;
        }

        foreach (var container in _objectList.GetRealizedContainers())
        {
            var index = _objectList.IndexFromContainer(container);
            if (index < 0 || index >= rows.Count)
            {
                continue;
            }

            var selected = IsObjectTreeRowSelected(rows[index]);
            if (container is ListBoxItem item)
            {
                item.IsSelected = selected;
            }

            container.GetVisualDescendants()
                .OfType<RecyclingObjectTreeRow>()
                .FirstOrDefault()
                ?.RefreshSelection();
        }
    }

    private void NameObjectContainer(Control? container, int index)
    {
        if (container is not ListBoxItem item
            || _objectList.ItemsSource is not IReadOnlyList<ObjectTreeRow> rows
            || index < 0
            || index >= rows.Count)
        {
            return;
        }

        var row = rows[index];
        var name = row.Kind switch
        {
            ObjectTreeRowKind.Breadcrumb => $"Открыта группа «{row.Title}», {row.Subtitle}",
            ObjectTreeRowKind.Group => $"Группа «{row.Title}», {row.Subtitle}",
            ObjectTreeRowKind.AssetKind => $"Ассет «{row.Title}», {row.Subtitle}",
            ObjectTreeRowKind.AssetKindBreadcrumb => $"Открыт ассет «{row.Title}», {row.Subtitle}",
            _ => $"{row.Title} · {row.Subtitle}",
        };
        AutomationProperties.SetName(item, name);
        item.IsSelected = IsObjectTreeRowSelected(row);
    }

    /// <summary>
    /// A recycled data-template host. Avalonia reuses the host/container while the bounded row
    /// content is rebuilt from the current DataContext, so no click handler can retain a scrolled-
    /// away object.
    /// </summary>
    private sealed class RecyclingObjectTreeRow : ContentControl
    {
        private readonly EditorView _owner;

        public RecyclingObjectTreeRow(EditorView owner)
        {
            _owner = owner;
            DataContextChanged += (_, _) => RefreshRow();
        }

        public void RefreshSelection()
        {
            if (DataContext is not ObjectTreeRow row || Content is not Control content)
            {
                return;
            }

            var selected = _owner.IsObjectTreeRowSelected(row);
            if (content is Button button)
            {
                Ui.SetSelected(button, selected);
            }
            else if (content is Border border)
            {
                Ui.SetStructureSelected(border, selected);
            }
        }

        private void RefreshRow()
        {
            if (DataContext is not ObjectTreeRow row)
            {
                Content = null;
                return;
            }

            Content = row.Kind switch
            {
                ObjectTreeRowKind.Breadcrumb => _owner.BuildGroupBreadcrumbRow(row),
                ObjectTreeRowKind.Group => _owner.BuildGroupRow(row),
                ObjectTreeRowKind.Water => _owner.BuildWaterRow(row),
                ObjectTreeRowKind.Road => _owner.BuildRoadRow(row),
                ObjectTreeRowKind.Land => _owner.BuildLandRow(row),
                ObjectTreeRowKind.AssetKind => _owner.BuildAssetKindRow(row),
                ObjectTreeRowKind.AssetKindBreadcrumb => _owner.BuildAssetKindBreadcrumbRow(row),
                _ => _owner.BuildObjectRow(row),
            };
            RefreshSelection();
        }
    }

    private static string ObjectIcon(SceneObjectKind kind) => kind switch
    {
        SceneObjectKind.Rectangle => MapIcons.ObjectRectangle,
        SceneObjectKind.Ellipse => MapIcons.ObjectEllipse,
        SceneObjectKind.Stamp => MapIcons.ObjectStamp,
        SceneObjectKind.Line => MapIcons.Line,
        SceneObjectKind.Shape => MapIcons.Shape,
        SceneObjectKind.Text => MapIcons.Text,
        SceneObjectKind.Decoration => MapIcons.Star,
        SceneObjectKind.Composition => MapIcons.ObjectComposition,
        _ => MapIcons.ObjectPlaceholder,
    };

    private void SyncAssetList()
    {
        _syncingLibrary = true;
        try
        {
            SyncLibraryDiagnostic();
            SyncLibraryFilterState();
            SyncCollectionTree();
            SyncBreadcrumbs();
            SyncLibraryGrid();
            SyncSelectedSet();
            SyncStampGestureReadout();
            SyncStampToolPanel();
        }
        finally
        {
            _syncingLibrary = false;
        }

        // Choosing a card brings twelve more files into view that the visible-range report never
        // saw, so the check is kicked here too. It is idempotent: the view model verifies only
        // what it has no verdict for, and returns at once when there is nothing left to check.
        if (_editor.CurrentVariant is { } current
            && _editor.AvailabilityOf(current.Sha256) == AssetAvailability.Unknown)
        {
            UiTasks.Forget(this, CheckVisibleAvailabilityAsync);
        }
    }

    private void SyncLibraryDiagnostic()
    {
        var (visible, text) = _editor.LibraryStatus switch
        {
            AssetLibraryStatus.Damaged => (true, "Индекс библиотеки повреждён и защищён от записи. "
                + _editor.LibraryDiagnostic),
            AssetLibraryStatus.Migrated => (true, _editor.LibraryDiagnostic),
            _ => (false, ""),
        };

        _libraryDiagnostic.IsVisible = visible;
        _libraryDiagnosticText.Text = text;
        AutomationProperties.SetName(_libraryDiagnosticText, visible ? text : "Библиотека в порядке");
    }

    private void SyncLibraryFilterState()
    {
        SyncLibraryToolbar();
        SetSegment(_placementModeButtons, _editor.StampPlacementMode switch
        {
            StampPlacementMode.Series => "Series",
            StampPlacementMode.Area => "Area",
            StampPlacementMode.Path => "Path",
            StampPlacementMode.FillArea => "FillArea",
            _ => "One",
        });

        SyncQuickFilters();
        SetToggle(_libraryFilterButtons["Favorites"], "Только избранное", _editor.LibraryFavoritesOnly);
        SetToggle(_libraryFilterButtons["Recent"], "Только недавние", _editor.LibraryRecentOnly);
        SetToggle(_libraryFilterButtons["Untagged"], "Только без тегов", _editor.LibraryUntaggedOnly);
        SetToggle(
            _libraryFilterButtons["UsedInMap"], "Только используемое на карте", _editor.LibraryUsedInMapOnly);
        SetToggle(
            _randomVariantToggle, "Случайный вариант после размещения", _editor.RandomVariantAfterPlacement);
        SetToggle(
            _randomFlipToggle, _editor.SelectedSetCanFlip ? "Случайное зеркальное отражение ассета"
                : "Отражение запрещено в паспорте ассета", _editor.StampRandomFlip && _editor.SelectedSetCanFlip);
        _randomFlipToggle.IsEnabled = _editor.SelectedSetCanFlip;
        SetToggle(_avoidOverlapToggle,
            _editor.StampAvoidOverlap ? "Без наложений: включено" : "Без наложений: выключено", _editor.StampAvoidOverlap);
        SetToggle(_randomRotationToggle,
            _editor.StampRandomAngle ? $"Случайный поворот: шаг {EditorLabels.RandomAngleStep(_editor.StampRandomAngleStep)}" : "Случайный поворот выключен",
            _editor.StampRandomAngle);

        SyncLibraryTagTree();
        FillChoices(
            _libraryMediaFilter, "Любой тип", _editor.LibraryMediaTypeChoices, _editor.LibraryMediaType);
        if ((_libraryImportedFrom.Text ?? "") != _editor.LibraryImportedFromText)
        {
            _libraryImportedFrom.Text = _editor.LibraryImportedFromText;
        }

        if ((_libraryImportedTo.Text ?? "") != _editor.LibraryImportedToText)
        {
            _libraryImportedTo.Text = _editor.LibraryImportedToText;
        }

        var dateHint = _editor.HasLibraryDateError
            ? "Дата не распознана. Ожидается ГГГГ-ММ-ДД, например 2026-08-26."
            : "Дата в формате ГГГГ-ММ-ДД.";
        _libraryDateHint.Text = dateHint;
        Ui.SetClass(_libraryDateHint, "danger", _editor.HasLibraryDateError);
        Ui.SetClass(_libraryDateHint, "faint", !_editor.HasLibraryDateError);
        AutomationProperties.SetName(_libraryDateHint, dateHint);
        _libraryDateHint.IsVisible = true;

        var summary = "Фильтры: " + _editor.ActiveLibraryFilterSummary;
        _libraryFilterSummary.Text = summary;
        AutomationProperties.SetName(_libraryFilterSummary, summary);
        _clearFiltersButton.IsEnabled = _editor.HasActiveLibraryFilters;

        static void FillChoices(
            ComboBox box, string anyLabel, IReadOnlyList<string> choices, string? selected)
        {
            var wanted = new List<string> { anyLabel };
            wanted.AddRange(choices);
            if (box.ItemsSource is not IReadOnlyList<string> current || !current.SequenceEqual(wanted))
            {
                box.ItemsSource = wanted;
            }

            box.SelectedItem = selected is not null && wanted.Contains(selected) ? selected : anyLabel;
        }

    }

    private static void SetSegment(Dictionary<string, Button> buttons, string activeKey)
    {
        foreach (var (key, button) in buttons)
        {
            Ui.SetSelected(button, key == activeKey);
        }
    }


    /// <summary>
    /// The breadcrumb trail. Each ancestor segment is a real button that navigates back up, so
    /// the trail is reachable from the keyboard and visible to UI Automation; a bare panel of
    /// text blocks would not appear in the accessibility tree at all.
    /// </summary>
    private void SyncBreadcrumbs()
    {
        _breadcrumbs.Children.Clear();
        _breadcrumbs.VerticalAlignment = VerticalAlignment.Center;

        var root = Ui.TextButton("BreadcrumbRoot", "Библиотека", () =>
        {
            _editor.LibraryCollectionId = null;
            _editor.SelectAssetSet(null);
            SyncAssetList();
        }, "compact");
        AutomationProperties.SetName(root, "Библиотека, все коллекции");
        ToolTip.SetTip(root, "Показать все коллекции");
        _breadcrumbs.Children.Add(root.Docked(Dock.Left));

        var current = "Все коллекции";
        if (_editor.LibraryCollectionId is { } collectionId
            && FindCollectionName(_editor.LibraryCollections, collectionId) is { } collectionName)
        {
            current = collectionName;
            _breadcrumbs.Children.Add(Separator().Docked(Dock.Left));
            var collection = Ui.TextButton("BreadcrumbCollection", collectionName, () =>
            {
                _editor.SelectAssetSet(null);
                SyncAssetList();
            }, "compact");
            AutomationProperties.SetName(collection, $"Коллекция {collectionName}");
            _breadcrumbs.Children.Add(collection.Docked(Dock.Left));
        }

        if (_editor.SelectedSet is { } selected)
        {
            current = selected.Name;
            _breadcrumbs.Children.Add(Separator().Docked(Dock.Left));
        }

        var leaf = new TextBlock
        {
            Text = current,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Classed("muted", "role-caption").Named("BreadcrumbCurrent");
        AutomationProperties.SetName(leaf, current);
        _breadcrumbs.Children.Add(leaf);

        static Control Separator() => new TextBlock
        {
            Text = "/",
            Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("faint");

        static string? FindCollectionName(IReadOnlyList<AssetCollectionNode> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id)
                {
                    return node.Name;
                }

                if (FindCollectionName(node.Children, id) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private void SyncLibraryGrid()
    {
        var rows = _editor.LibraryRows;
        if (!ReferenceEquals(_gallerySourceRows, rows))
        {
            _reportedLibraryRange = (-1, -1);
            _gallerySourceRows = rows;
            RebuildGalleryRows();
        }

        _libraryGrid.SelectedIndex = -1;

        // The grid stays in the accessibility tree even when empty - a control that disappears
        // cannot be found, focused or reported by a screen reader.
        var cards = _editor.LibrarySets.Count;
        if (_regionalLibraryEmpty is not null) _regionalLibraryEmpty.IsVisible = cards == 0
            && !_editor.HasActiveLibraryFilters && _editor.LibraryStatus != AssetLibraryStatus.Damaged;
        // The gallery scrolls in the space left by filters and details; a forced minimum overlaps them.
        _libraryGrid.MinHeight = 0;
        _libraryGrid.ClipToBounds = true;

        // The UIA name opens with the exact words on screen; the empty states then add the fix
        // a screen-reader user would otherwise only get from the surrounding visuals.
        var accessibleCount = _editor.LibraryStatus == AssetLibraryStatus.Damaged
            ? "Библиотека недоступна"
            : cards == 0
                ? (_editor.HasActiveLibraryFilters
                    ? "Ничего не найдено. Измените запрос или снимите фильтры."
                    : "Библиотека пуста. Импортируйте PNG, JPEG или WebP.")
                : $"Ассеты · {cards}";
        _libraryCountText.Text = _editor.LibraryStatus == AssetLibraryStatus.Damaged
            ? "Библиотека недоступна"
            : cards == 0
                ? (_editor.HasActiveLibraryFilters ? "Ничего не найдено" : "Библиотека пуста")
                : $"Ассеты · {cards}";
        AutomationProperties.SetName(_libraryCountText, accessibleCount);
        ToolTip.SetTip(_libraryCountText, accessibleCount);
        AutomationProperties.SetName(_libraryGrid, $"Карточки библиотеки, {cards}");
    }
    private void SyncSelectedSet()
    {
        SyncAppearanceSections();
        var set = _editor.SelectedSet;
        var hasSet = set is not null;
        _selectedSetBlock.IsVisible = hasSet;
        _stampGestureBlock.IsVisible = hasSet || _editor.SelectedEditablePath is not null || _editor.CurrentAreaSource is not null;
        _setManagementDetails.IsVisible = hasSet;
        _selectedAsset = _editor.CurrentVariant is { } current
            ? _editor.Assets.FirstOrDefault(record =>
                string.Equals(record.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))
            : null;

        _selectedSetNameText.Text = set?.Name ?? "Набор не выбран";
        // Named explicitly: a bare TextBlock is not reliably reported, and the name of the card
        // the tool is holding is exactly what a screen reader has to be able to read back.
        AutomationProperties.SetName(_selectedSetNameText, _selectedSetNameText.Text);
        ToolTip.SetTip(_selectedSetNameText, _selectedSetNameText.Text);
        _setBadge.IsVisible = set is { IsSet: true };
        if (set is { IsSet: true })
        {
            // A TextBlock reports its own Text to UI Automation, so a bare digit here would be
            // exactly the mute badge the catalog research rules out. The panel has room for the
            // whole phrase, so the badge simply says it.
            var badgeName = $"{set.VariantCount} вариантов";
            _setBadgeText.Text = badgeName;
            AutomationProperties.SetName(_setBadgeText, badgeName);
            ToolTip.SetTip(_setBadge, badgeName);
        }

        SetToggle(
            _favoriteButton, set?.Favorite == true ? "Убрать из избранного" : "В избранное",
            set?.Favorite == true);
        _favoriteButton.IsEnabled = set is not null;

        _variantPositionText.Text = _editor.CurrentVariantLabel.Length == 0
            ? "Вариант не выбран"
            : $"Вариант {_editor.CurrentVariantLabel}"
                + (_editor.IsCurrentVariantTheCover ? " · обложка" : "");
        AutomationProperties.SetName(_variantPositionText, _variantPositionText.Text);

        var many = _editor.SelectedSetVariants.Count > 1;
        _previousVariantButton.IsEnabled = many;
        _nextVariantButton.IsEnabled = many;

        // A singleton card has nothing to reorder and nothing to choose a cover from, so those
        // controls are visibly unavailable rather than silently doing nothing.
        _setRenameBox.IsEnabled = _editor.CanRenameSelectedSet;
        _setRenameButton.IsEnabled = _editor.CanRenameSelectedSet;
        if (!_setRenameBox.IsFocused)
        {
            _setRenameBox.Text = set?.Name ?? "";
        }

        _variantEarlierButton.IsEnabled = _editor.CanMoveCurrentVariant(-1);
        _variantLaterButton.IsEnabled = _editor.CanMoveCurrentVariant(1);
        _setCoverButton.IsEnabled = _editor.CanMakeCurrentVariantCover;
        SetCoverButtonLabel(set);
        SyncSelectedSetAvailability();

        _assetSelectionText.Text = _selectedAsset is { } selected
            ? $"{selected.DisplayName} · {selected.Width}×{selected.Height}"
            : "Ассет не выбран. Выберите карточку в списке выше.";
        AutomationProperties.SetName(_assetSelectionText, _assetSelectionText.Text);

        SyncVariantStrip();

        foreach (var command in _assetSelectionCommands)
        {
            command.IsEnabled = _selectedAsset is not null;
        }
    }

    /// <summary>The cover button says which state it would move the card into, or why it cannot.</summary>
    private void SetCoverButtonLabel(AssetSetSummary? set)
    {
        var title = set is null
            ? "Сделать обложкой: набор не выбран"
            : _editor.IsCurrentVariantTheCover
                ? "Этот вариант уже обложка набора"
                : set.IsSet
                    ? $"Сделать вариант {_editor.CurrentVariantLabel} обложкой набора"
                    : "У набора из одного варианта обложка одна";
        AutomationProperties.SetName(_setCoverButton, title);
        ToolTip.SetTip(_setCoverButton, title);
    }

    private void SyncVariantStrip()
    {
        var variants = _editor.SelectedSetVariants;
        if (ReferenceEquals(_variantStrip.Tag, variants)) return;
        _variantStrip.Tag = variants;
        _variantStrip.ItemTemplate = new FuncDataTemplate<CompactVariant[]>((row, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var cell in row ?? []) panel.Children.Add(BuildVariantChoice(cell.Variant, cell.Index, "Variant_", 40));
            return panel;
        }, supportsRecycling: false);
        _variantStrip.ItemsSource = variants.Select((v, i) => new CompactVariant(v, i)).Chunk(6).ToArray();
    }

    private ToggleButton BuildVariantChoice(
        AssetVariant variant,
        int position,
        string namePrefix,
        double previewSize,
        bool stampFamily = false)
    {
        var picture = new Image { Stretch = Stretch.Uniform };
        _thumbnails.Assign(picture, _editor.LibraryThumbnailPathForVariant(variant.Id));
        var isCover = !stampFamily && variant.Id == _editor.SelectedSet?.CoverVariantId;
        var availability = _editor.AvailabilityOf(variant.Sha256);
        var flagged = availability is not (AssetAvailability.Unknown or AssetAvailability.Available);

        // The number is what the pager and the shortcuts count by, so the cell says it.
        Control face = new Panel
        {
            Width = previewSize,
            Height = previewSize,
            Children =
            {
                picture,
                new TextBlock
                {
                    Text = (position + 1).ToString(), FontSize = 10, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 0, 0, 0),
                }.Classed("muted"),
            },
        };

        // Cover and file-state marks remain distinct from the selected outline.
        if (isCover || flagged)
        {
            var mark = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(2, 0),
                Child = flagged ? MapIcons.Stroked(MapIcons.Warning, 11)
                    : new TextBlock { Text = "обл.", FontSize = 12 },
            }.Classed("badge");
            ToolTip.SetTip(mark, flagged ? EditorViewModel.AvailabilityText(availability) : "Обложка набора");
            if (availability is AssetAvailability.Missing or AssetAvailability.HashMismatch)
            {
                mark.Classes.Add("danger");
            }
            else if (flagged)
            {
                mark.Classes.Add("warn");
            }

            face = new Panel { Children = { face, mark } };
        }

        var button = new ToggleButton { Content = face }
            .Named(namePrefix + variant.Id).Classed("segment");
        var selected = variant.Id == _editor.CurrentVariant?.Id;
        var count = stampFamily ? _editor.StampFamilyVariants.Count : _editor.SelectedSetVariants.Count;
        var label = stampFamily ? _editor.StampFamilyVariants[position].SetName : variant.Label;
        var name = $"{label} · вариант {position + 1} из {count}"
            + (isCover ? ", обложка" : "")
            + (selected ? ", текущий" : "")
            + (flagged ? ", " + EditorViewModel.AvailabilityText(availability) : "");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        Ui.SetSelected(button, selected);
        button.Click += (_, _) =>
        {
            if (stampFamily) _editor.SelectStampFamilyVariantAt(position);
            else _editor.SelectVariantAt(position);
            SyncAssetList();
            SyncStatus();
        };
        return button;
    }

    private void SyncNoteList()
    {
        if (_noteSelectionEditor is not null)
            _noteSelectionEditor.IsVisible = _selectedNoteId is { } selected
                && _editor.Session.Document.Notes.Any(note => note.Id == selected);
        var query = (_noteSearch.Text ?? "").Trim();
        _noteList.Children.Clear();

        var notes = _editor.SearchNotes(query).ToList();
        if (notes.Count == 0)
        {
            _noteList.Children.Add(Ui.EmptyState(
                "NoNotesText",
                MapIcons.Notes,
                query.Length == 0 ? "Заметок пока нет" : "Ничего не найдено",
                query.Length == 0
                    ? "Добавьте заметку в центре экрана или привяжите её к выделенному объекту."
                    : "Измените запрос или очистите поле поиска."));
            return;
        }

        foreach (var note in notes)
        {
            var states = new List<(string, string)>();
            if (note.IsDetached(_editor.Session.Document))
            {
                states.Add((MapIcons.Warning, "Объект-якорь удалён"));
            }

            var captured = note;
            var row = Ui.ListRow(
                "Note_" + note.Id,
                MapIcons.Notes,
                note.Title,
                null,
                states,
                () =>
                {
                    _selectedNoteId = captured.Id;
                    _noteTitle.Text = captured.Title;
                    _noteMarkdown.Text = captured.Markdown;
                    SyncNoteList();
                    SyncNoteWiki();
                },
                note.Title);

            Ui.SetSelected(row, note.Id == _selectedNoteId);
            _noteList.Children.Add(row);
        }
    }

    private Button? _createVersionButton;
    private readonly TextBlock _checkpointCountText = new TextBlock
    {
        Name = "CheckpointCountText", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
    }.Classed("faint", "role-caption");

    private void SyncCheckpointList()
    {
        var count = _editor.Checkpoints.Count;
        _checkpointCountText.Text = $"{count} из {MapCheckpointLimits.MaxCount}";
        if (_createVersionButton is not null)
        {
            _createVersionButton.IsEnabled = count < MapCheckpointLimits.MaxCount;
            ToolTip.SetTip(_createVersionButton, count < MapCheckpointLimits.MaxCount
                ? "Сохранить текущее состояние карты как версию"
                : $"Достигнут лимит {MapCheckpointLimits.MaxCount} версий: удалите ненужную, чтобы сделать новый снимок");
        }
        _checkpointList.Children.Clear();
        _checkpointPreviews.Prune([.. _editor.Checkpoints.Select(item => item.Id)]);

        if (_editor.Checkpoints.Count == 0)
        {
            _checkpointList.Children.Add(Ui.EmptyState(
                "NoCheckpointsText",
                MapIcons.Save,
                "Версий пока нет",
                "Нажмите «Создать версию», чтобы сохранить текущее состояние и миниатюру."));
            return;
        }

        foreach (var checkpoint in _editor.Checkpoints.Reverse())
        {
            var captured = checkpoint;
            var preview = _checkpointPreviews.Image(checkpoint);
            Control previewControl = preview is null
                ? MapIcons.Stroked(MapIcons.Warning, 22)
                : new Image
                {
                    Source = preview,
                    Stretch = Stretch.Uniform,
                };
            AutomationProperties.SetName(previewControl, $"Миниатюра версии {checkpoint.Name}");

            var renameBox = new TextBox
            {
                Name = "VersionName_" + checkpoint.Id,
                Text = checkpoint.Name,
                MaxLength = MapCheckpointLimits.MaxNameLength,
            };
            AutomationProperties.SetName(renameBox, "Новое название версии");
            renameBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                _editor.RenameVersion(captured.Id, renameBox.Text);
                SyncCheckpointList();
                SyncStatus();
            };
            var rename = Ui.TextButton(
                "RenameVersion_" + checkpoint.Id,
                "Переименовать",
                () =>
                {
                    _editor.RenameVersion(captured.Id, renameBox.Text);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "compact");

            var compare = Ui.TextButton("CompareCheckpoint_" + checkpoint.Id, "Сравнить", () =>
            {
                _editor.CompareWithCheckpoint(captured.Id);
                SyncCompareBlock();
            }, "compact");
            ToolTip.SetTip(compare, "Что изменилось с этой версии: итог словами, подсветка на карте, «До / После».");
            var export = Ui.AsyncButton("ExportChanges_" + checkpoint.Id, "Выгрузить изменения", () => ExportChangesAsync(captured), "compact");
            ToolTip.SetTip(export, "Файл .mapchanges с тем, что изменилось с этой версии, — для другой копии карты (без сети).");
            var restore = Ui.AsyncButton(
                "RestoreCheckpoint_" + checkpoint.Id,
                "Восстановить",
                async () =>
                {
                    await _editor.RestoreCheckpointAsync(captured.Id);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "compact");
            var delete = Ui.AsyncButton(
                "DeleteCheckpoint_" + checkpoint.Id,
                "Удалить",
                async () =>
                {
                    await _editor.DeleteCheckpointAsync(captured.Id);
                    SyncCheckpointList();
                    SyncStatus();
                },
                "destructive",
                "compact");

            var renameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8, Children = { renameBox, rename } };
            Grid.SetColumn(rename, 1);

            var card = new Border
            {
                Padding = new Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        new Border
                        {
                            Height = 128,
                            Background = Application.Current?.FindResource("MapFieldBrush") as IBrush,
                            Child = previewControl,
                        }.Classed("group"),
                        new TextBlock
                        {
                            Text = checkpoint.Name,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }.Classed("role-panel"),
                        new TextBlock
                        {
                            Text = checkpoint.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                        }.Classed("faint", "role-caption"),
                        new Expander { Header = "Детали версии", Content = new TextBlock
                        {
                            Text = $"Миниатюра: {Math.Max(1, checkpoint.PreviewPng.Length / 1024)} КиБ · "
                                + $"объектов: {checkpoint.Snapshot.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count)}",
                            TextWrapping = TextWrapping.Wrap,
                        }.Classed("faint", "role-caption") },
                        renameRow,
                        new Ui.ChoiceGrid
                        {
                            MaxColumns = 3, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                            Children = { compare, export, restore, delete },
                        },
                    },
                },
            }.Classed("group").Named("Checkpoint_" + checkpoint.Id);
            AutomationProperties.SetName(
                card,
                $"Версия {checkpoint.Name}, создана "
                    + checkpoint.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            _checkpointList.Children.Add(card);
        }
    }
}
