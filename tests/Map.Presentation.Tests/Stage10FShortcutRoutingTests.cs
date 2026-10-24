using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class Stage10FShortcutRoutingTests
{
    [Fact]
    public void F11SwitchesFullScreenEverywhereAndF10StaysWithWindows()
    {
        var shortcuts = new EditorShortcutSettings();
        Assert.Equal("F11", shortcuts.Get(EditorShortcutSettings.Commands.FullScreen));
        foreach (var context in new[] { ShortcutContext.Dashboard, ShortcutContext.Editor })
        {
            Assert.Equal(EditorShortcutSettings.Commands.FullScreen,
                shortcuts.Resolve(new ShortcutInput("F11", ShortcutModifiers.None), new HashSet<ShortcutContext> { context }));
        }
        Assert.True(ShortcutGesture.TryParse("ctrl+f5", out var gesture));
        Assert.Equal("Ctrl+F5", gesture.ToString());
        Assert.False(ShortcutGesture.TryParse("F10", out _));
    }

    [Fact]
    public void CatalogOpensFromAnyEditorToolAndEnterCompletesCanvasContours()
    {
        var shortcuts = new EditorShortcutSettings();
        foreach (var context in new[] { ShortcutContext.Editor, ShortcutContext.Brush,
                     ShortcutContext.Mask, ShortcutContext.Text, ShortcutContext.Land, ShortcutContext.Stamp })
        {
            Assert.Equal(EditorShortcutSettings.Commands.StampFocusLibrary,
                shortcuts.Resolve(new ShortcutInput("F", ShortcutModifiers.None),
                    new HashSet<ShortcutContext> { ShortcutContext.Editor, context }));
        }
        Assert.Null(shortcuts.Resolve(new ShortcutInput("F", ShortcutModifiers.None),
            new HashSet<ShortcutContext> { ShortcutContext.Dashboard }));
        Assert.Equal(EditorShortcutSettings.Commands.FinishPath,
            shortcuts.Resolve(new ShortcutInput("Enter", ShortcutModifiers.None),
                new HashSet<ShortcutContext> { ShortcutContext.Editor, ShortcutContext.Canvas }));
        Assert.True(ShortcutGesture.TryParse("Enter", out _));
        Assert.Empty(shortcuts.GetConflicts());
    }

    [Fact]
    public void RebindingTextToolStopsTheOldKeyAndStartsTheNewKey()
    {
        var shortcuts = new EditorShortcutSettings();
        shortcuts.Set("text", "Alt+T");
        var contexts = new HashSet<ShortcutContext> { ShortcutContext.Editor };

        Assert.Null(shortcuts.Resolve(new ShortcutInput("T", ShortcutModifiers.None), contexts));
        Assert.Equal(
            EditorShortcutSettings.Commands.Text,
            shortcuts.Resolve(new ShortcutInput("T", ShortcutModifiers.Alt), contexts));
    }

    [Fact]
    public void FontCycleResolvesWhileTheCreatedTextIsSelectedInSelectMode()
    {
        var shortcuts = new EditorShortcutSettings();
        var contexts = new HashSet<ShortcutContext>
        {
            ShortcutContext.Editor,
            ShortcutContext.Canvas,
            ShortcutContext.Selection,
        };

        Assert.Equal(
            EditorShortcutSettings.Commands.FontNext,
            shortcuts.Resolve(new ShortcutInput("RightBracket", ShortcutModifiers.None), contexts));
    }
}
