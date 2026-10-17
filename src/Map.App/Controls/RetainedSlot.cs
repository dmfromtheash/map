using System.Collections.Generic;
using Avalonia.Controls;

namespace Map.App.Controls;

/// <summary>
/// Shows one prebuilt control at a time, like a <see cref="ContentControl"/>, but keeps every
/// control it has already shown attached and only hides the others.
/// </summary>
/// <remarks>
/// Moving a large panel in and out of the visual tree makes Avalonia re-run style matching,
/// template application and the first text layout for every control in it: 80-150 ms for the
/// inspector or the stamp tool panel on the owner's PC, paid on every selection or tool change.
/// A hidden child is skipped by layout, rendering, hit testing and keyboard navigation, so the
/// visible result is the same. The slot never shows two children at once.
/// </remarks>
public sealed class RetainedSlot : Panel
{
    private readonly Dictionary<Control, Decorator> _hosts = new(ReferenceEqualityComparer.Instance);
    private Control? _content;

    /// <summary>The control currently shown, or null for an empty slot.</summary>
    public Control? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
            {
                return;
            }

            _content = value;
            if (value is not null && !_hosts.ContainsKey(value))
            {
                var host = new Decorator { Child = value };
                _hosts[value] = host;
                Children.Add(host);
            }

            foreach (var (child, host) in _hosts)
            {
                // Hiding the host also releases keyboard focus held inside it (Avalonia clears
                // the focus of an element that stops being visible), as detaching used to.
                host.IsVisible = ReferenceEquals(child, value);
            }
        }
    }
}
