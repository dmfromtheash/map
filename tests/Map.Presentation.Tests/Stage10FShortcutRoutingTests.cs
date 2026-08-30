using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class Stage10FShortcutRoutingTests
{
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
