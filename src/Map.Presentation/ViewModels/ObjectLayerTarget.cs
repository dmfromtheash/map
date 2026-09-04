using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// One object layer the current selection could be moved to, named the way the layers panel names
/// it. The view binds this instead of a raw <see cref="LayerId"/>, so no internal key ever reaches
/// the user.
/// <para>
/// Hidden and locked layers stay in the list on purpose: a selector that silently omits them makes
/// the user hunt for a layer that is simply not offered. They are listed with the reason and
/// refused if chosen - <see cref="Available"/> is what the control disables.
/// </para>
/// </summary>
public sealed record ObjectLayerTarget(
    LayerId Id,
    string Name,
    bool IsCurrent,
    bool Available = true,
    string? Unavailable = null)
{
    /// <summary>The layer's name plus the reason it cannot be chosen, ready for a list row.</summary>
    public string Display => Available || Unavailable is null ? Name : $"{Name} · {Unavailable}";
}
