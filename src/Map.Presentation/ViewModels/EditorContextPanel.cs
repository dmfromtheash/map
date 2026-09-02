namespace Map.Presentation.ViewModels;

/// <summary>What the single context area is showing right now.</summary>
public enum EditorContextPanelKind
{
    /// <summary>The settings of the active tool.</summary>
    ToolPanel,

    /// <summary>The Select tool with nothing selected: the empty-state note plus its settings.</summary>
    SelectIdle,

    /// <summary>The inspector of exactly one selected object.</summary>
    ObjectInspector,

    /// <summary>The shared inspector of several selected objects.</summary>
    MultiSelectionInspector,
}

/// <summary>
/// Decides which of the two things the one context area shows: the properties of the current
/// selection, or the settings of the active tool.
/// <para>
/// This used to be inline in the view, gated on <c>ActiveTool == Select</c>. That made a selected
/// object invisible in the panel the moment any drawing tool was active - so drawing a shape left
/// the user looking at the Shape tool while the shape itself was already selected, and the object
/// read as "just a leftover of the tool" instead of a thing with properties. The accepted UX rule
/// (docs/03) is the opposite: selecting an object shows its inspector <em>without</em> hiding the
/// way back to the tool's settings. Hence one selector plus an explicit user-facing toggle.
/// </para>
/// </summary>
public static class EditorContextPanel
{
    /// <summary>
    /// <paramref name="preferToolPanel"/> is the user's own choice inside the toggle. It is reset
    /// to false when the selection changes and to true when the active tool changes, so both
    /// actions keep an immediate visible effect.
    /// </summary>
    public static EditorContextPanelKind Resolve(
        EditorTool tool,
        int selectionCount,
        bool hasSelectionKind,
        bool preferToolPanel)
    {
        if (selectionCount > 0 && !preferToolPanel)
        {
            if (selectionCount > 1)
            {
                return EditorContextPanelKind.MultiSelectionInspector;
            }

            if (hasSelectionKind)
            {
                return EditorContextPanelKind.ObjectInspector;
            }
        }

        return tool == EditorTool.Select && selectionCount == 0
            ? EditorContextPanelKind.SelectIdle
            : EditorContextPanelKind.ToolPanel;
    }

    /// <summary>True when both halves of the toggle are meaningful, i.e. something is selected.</summary>
    public static bool ToggleApplies(int selectionCount) => selectionCount > 0;
}
