using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// DEC-070 step 3: one row of sections - «Рисовать · Объекты · Мир · Оформление». A section decides which tools the
/// rail shows and which panels stand beside the map: «Мир» opens its signed list, «Оформление» the Свет/Стили/Море
/// ribbon under the map. Switching never edits the map, the history or the active tool; picking a tool by its key
/// opens the section its button lives in.
/// </summary>
public sealed partial class EditorView
{
    private EditorWorkspace _workspace = EditorWorkspace.Draw;
    private readonly Dictionary<EditorWorkspace, Button> _workspaceTabs = [];
    private readonly List<(EditorRailEntry Entry, Control Button)> _railButtons = [];
    private readonly List<(Control Divider, EditorRailEntry[] Group)> _railDividers = [];
    private Control? _appearanceRibbon;

    public EditorWorkspace Workspace => _workspace;

    // The «Мир» tab keeps the name of the button it replaced, so every route into the world list stays one click.
    private static string WorkspaceTabName(EditorWorkspace workspace) => workspace switch
    {
        EditorWorkspace.Objects => "WorkspaceObjectsTab",
        EditorWorkspace.World => "WorldSectionButton",
        EditorWorkspace.Appearance => "WorkspaceAppearanceTab",
        _ => "WorkspaceDrawTab",
    };

    private Control BuildWorkspaceTabs()
    {
        var row = new StackPanel { Name = "WorkspaceTabs", Orientation = Orientation.Horizontal, Spacing = 2 };
        void Tab(EditorWorkspace workspace, string name, string title)
        {
            var tab = Ui.TextButton(name, title, () => ShowWorkspace(workspace, openWorldList: true), "workspace-tab");
            AttachInsideCard(tab, () => WorkspaceCard(workspace), Avalonia.Controls.PlacementMode.Bottom);
            _workspaceTabs[workspace] = tab;
            row.Children.Add(tab);
        }
        Tab(EditorWorkspace.Draw, WorkspaceTabName(EditorWorkspace.Draw), "Рисовать");
        // A hex map has no catalog objects: its placement is the hex tool itself.
        if (!_isHexWorkshop) Tab(EditorWorkspace.Objects, WorkspaceTabName(EditorWorkspace.Objects), "Объекты");
        Tab(EditorWorkspace.World, WorkspaceTabName(EditorWorkspace.World), "Мир");
        // The hex constructor has no Свет/Стили/Море ribbon, so it has no «Оформление» either.
        if (!_isHexWorkshop) Tab(EditorWorkspace.Appearance, WorkspaceTabName(EditorWorkspace.Appearance), "Оформление");
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.ActiveTool)) FollowActiveTool();
        };
        return row;
    }

    /// <summary>Opens a section. Only what is shown changes; a pressed «Мир» also opens its list.</summary>
    public void ShowWorkspace(EditorWorkspace workspace, bool openWorldList = false)
    {
        _workspace = workspace;
        foreach (var (key, tab) in _workspaceTabs) Ui.SetClass(tab, "active", key == workspace);
        foreach (var (entry, button) in _railButtons)
            button.IsVisible = EditorSurfaceLayout.InWorkspace(entry, workspace);
        foreach (var (divider, group) in _railDividers)
            divider.IsVisible = group.Any(entry => EditorSurfaceLayout.InWorkspace(entry, workspace) && entry.Tool != EditorTool.Select);
        if (_appearanceRibbon is not null) SyncAppearanceRibbon();
        if (workspace != EditorWorkspace.World) ShowWorldSection(false);
        else if (openWorldList) ShowWorldSection(true);
    }

    // A tool picked by its key (or by a command) whose button is in another section brings that section forward.
    private void FollowActiveTool()
    {
        var tool = _editor.ActiveTool;
        if (tool == EditorTool.Select || !EditorSurfaceLayout.OffersTool(_isHexWorkshop, tool)) return;
        var target = EditorSurfaceLayout.WorkspaceOf(tool);
        if (target != _workspace) ShowWorkspace(target);
    }
}
