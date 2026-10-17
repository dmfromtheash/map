using Avalonia.Input;
using Map.Presentation.ViewModels;

namespace Map.App.Services;

internal static class ShortcutKeyAdapter
{
    public static bool TryCreate(KeyEventArgs args, out ShortcutInput input)
    {
        input = null!;
        var key = args.PhysicalKey switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => args.PhysicalKey.ToString(),
            PhysicalKey.Slash => "Slash",
            PhysicalKey.Comma => "Comma",
            PhysicalKey.Period => "Period",
            PhysicalKey.Backquote => "Backquote",
            PhysicalKey.BracketLeft => "LeftBracket",
            PhysicalKey.BracketRight => "RightBracket",
            _ => KeyName(args.Key),
        };
        if (key is null)
        {
            return false;
        }

        var modifiers = ShortcutModifiers.None;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ShortcutModifiers.Control;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ShortcutModifiers.Alt;
        if ((args.KeyModifiers & ~(KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) != 0)
        {
            return false;
        }

        input = new ShortcutInput(key, modifiers);
        return true;
    }

    private static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Delete => "Delete",
            Key.Back => "Backspace",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => null,
        };
    }
}
