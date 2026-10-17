using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Map.App.Controls;
using Map.App.Views;
using Map.Presentation.ViewModels;

namespace Map.App.Services;

/// <summary>
/// The keyboard path of the editor, in one production place so it can be driven by a real key
/// event outside <see cref="MainWindow"/>.
/// <para>
/// It used to live entirely inside the window, which meant a smoke run could only call the view
/// model method a key was supposed to reach - proving the method, never the key. Everything here
/// is what the window itself runs: the typing guard, the active contexts, the user's shortcut
/// table and the commands that need nothing but the editor view. Window-level commands (files,
/// dialogs, export) stay in <see cref="MainWindow"/> and are not handled here.
/// </para>
/// </summary>
public static class EditorKeyRouting
{
    /// <summary>A key pressed inside a text box is text, never a map command.</summary>
    public static bool IsTypingInText(TopLevel? root, KeyEventArgs? key = null) => key?.Source switch
    {
        TextBox => true,
        MapCanvasControl => false,
        _ => root?.FocusManager?.GetFocusedElement() is TextBox,
    };

    /// <summary>The shortcut contexts the editor is in right now.</summary>
    public static IReadOnlySet<ShortcutContext> Contexts(EditorView? view, KeyEventArgs? key = null)
    {
        var contexts = new HashSet<ShortcutContext>();
        if (view is null)
        {
            contexts.Add(ShortcutContext.Dashboard);
            return contexts;
        }

        contexts.Add(ShortcutContext.Editor);
        // Tab navigation can move focus before this bubbling event reaches MainWindow. Its
        // original canvas source still owns that key; Tab inside controls remains navigation.
        if (view.Canvas.IsFocused || ReferenceEquals(key?.Source, view.Canvas)) contexts.Add(ShortcutContext.Canvas);

        // Water systems are a selection too: without this, Delete never even resolved while a
        // river was the only thing selected.
        if (view.Editor.SelectionCount > 0 || view.Editor.WaterSelectionCount > 0 || view.Editor.RoadSelectionCount > 0
            || view.Editor.LandSelectionCount > 0)
            contexts.Add(ShortcutContext.Selection);
        if (view.IsNotesOpen) contexts.Add(ShortcutContext.Notes);
        contexts.Add(view.Editor.ActiveTool switch
        {
            EditorTool.Brush => ShortcutContext.Brush,
            EditorTool.Mask => ShortcutContext.Mask,
            EditorTool.Land => ShortcutContext.Land,
            EditorTool.Text => ShortcutContext.Text,
            EditorTool.Stamp => ShortcutContext.Stamp,
            _ => ShortcutContext.Editor,
        });
        return contexts;
    }

    /// <summary>
    /// Resolves one real key event against the user's shortcut table and runs it when it is an
    /// editor-only command. Returns false for anything the hosting window has to handle itself.
    /// </summary>
    public static bool TryHandleKey(TopLevel? root, EditorView view, EditorShortcutSettings shortcuts, KeyEventArgs args)
    {
        System.ArgumentNullException.ThrowIfNull(view);
        System.ArgumentNullException.ThrowIfNull(shortcuts);
        System.ArgumentNullException.ThrowIfNull(args);
        if (IsTypingInText(root, args) || !ShortcutKeyAdapter.TryCreate(args, out var input)) return false;
        var command = shortcuts.Resolve(input, Contexts(view, args));
        return command is not null && TryExecuteEditorCommand(view, command);
    }

    /// <summary>The commands that need only the editor view. Everything else returns false.</summary>
    public static bool TryExecuteEditorCommand(EditorView view, string command)
    {
        System.ArgumentNullException.ThrowIfNull(view);
        var editor = view.Editor;
        switch (command)
        {
            case EditorShortcutSettings.Commands.Delete:
            case EditorShortcutSettings.Commands.DeleteAlternate:
                // One entry point for the whole editor: it removes the selected node, channel or
                // water systems at the chosen level, and otherwise the selected objects.
                editor.DeleteSelection();
                return true;
            case EditorShortcutSettings.Commands.Escape:
                editor.HandleEscape();
                return true;
            case EditorShortcutSettings.Commands.FinishPath:
                if (editor.HasRiverPreview) editor.ApplyRiverPreview();
                else if (editor.IsCreatingRiver) editor.FinishRiver();
                else if (editor.IsCreatingRoad) editor.FinishRoad();
                else if (editor.IsCreatingEditableArea) editor.FinishEditableArea();
                else if (editor.IsCreatingEditablePath) editor.FinishEditablePath();
                else editor.FinishPenPath();
                return true;
            default:
                return false;
        }
    }
}
