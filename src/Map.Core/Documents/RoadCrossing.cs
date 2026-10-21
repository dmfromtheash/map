namespace Map.Core.Documents;

/// <summary>What a road does where it meets a river (R06). A crossing itself is derived from the
/// geometry; only the kind chosen by hand is stored on the road.</summary>
public enum RoadCrossingKind
{
    /// <summary>The default: a deck with railings over the water.</summary>
    Bridge = 0,
    /// <summary>A light shallow band: the road wades through.</summary>
    Ford = 1,
    /// <summary>A rope across the water; no deck.</summary>
    Ferry = 2,
    /// <summary>Nothing drawn: the road simply stops at the water (or the water hides it).</summary>
    None = 3,
}

/// <summary>
/// The kind chosen for one crossing: the <paramref name="Ordinal"/>-th place (along the branch)
/// where this branch meets this channel. Stored only when it departs from the default bridge.
/// </summary>
public sealed record RoadCrossingOverride(Guid BranchId, Guid ChannelId, int Ordinal, RoadCrossingKind Kind);
