using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// One control point of a channel centreline in world units. <see cref="Width"/> is the full
/// channel width at this node; between nodes it is interpolated along the channel length.
/// <see cref="Sharp"/> makes the centreline pass through the node as a corner, not a curve.
/// <para>
/// <see cref="Tension"/> is the curvature handle of this node: it scales the tangent the spline
/// leaves and enters the node with. 1 is the plain Catmull-Rom curve, 0 is a corner (what
/// <see cref="Sharp"/> forces), and above 1 the bend swings wider. It is what makes a smooth
/// course editable without adding control points: shape is changed here, never by subdividing.
/// </para>
/// </summary>
public sealed record RiverNode(Guid Id, double X, double Y, double Width, bool Sharp = false, double Tension = 1)
{
    public const double MinimumTension = 0;
    public const double MaximumTension = 2.5;

    /// <summary>The tangent scale actually used by the geometry: a sharp node always reads 0.</summary>
    public double EffectiveTension => Sharp ? 0 : Math.Clamp(Tension, MinimumTension, MaximumTension);
}

/// <summary>
/// A point on another channel of the same river: the segment that starts at
/// <see cref="ParentNodeId"/>, at <see cref="Fraction"/> of that segment's length. Addressing the
/// segment by node id keeps the junction in place when the parent is edited anywhere else.
/// </summary>
public sealed record RiverJunction(Guid ParentChannelId, Guid ParentNodeId, double Fraction);

/// <summary>
/// One watercourse of a river: its own nodes from source to mouth, no pixels. A channel may
/// flow out of another channel (<see cref="Source"/>: a distributary or a lake outflow), flow
/// into one (<see cref="Mouth"/>: a tributary), or be a closed lake (<see cref="Closed"/>).
/// </summary>
public sealed record RiverChannel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Русло";
    public ImmutableArray<RiverNode> Nodes { get; init; } = [];

    /// <summary>Null: a free source. Otherwise the channel starts on another channel or lake.</summary>
    public RiverJunction? Source { get; init; }

    /// <summary>Null: a free end (sea, lake or map edge). Otherwise the channel is a tributary.</summary>
    public RiverJunction? Mouth { get; init; }

    /// <summary>A lake: the nodes form a closed outline and node widths are ignored.</summary>
    public bool Closed { get; init; }
}

/// <summary>Appearance evaluated at render time from the channel geometry; never baked.</summary>
public sealed record RiverStyle
{
    public string WaterColor { get; init; } = "#FF4E86A3";
    public double Opacity { get; init; } = 1;
    public string BankColor { get; init; } = "#FF243F4E";
    public double BankWidth { get; init; } = 1.5;

    /// <summary>Soft dark halo outside the banks, like the ink bleed of a drawn river.</summary>
    public double BankShadowOpacity { get; init; } = 0.28;
    public double BankShadowBlur { get; init; } = 4;

    /// <summary>Thin light flow strokes inside the water; 0 disables them.</summary>
    public string HighlightColor { get; init; } = "#FFDDEFF4";
    public double HighlightOpacity { get; init; } = 0.55;

    /// <summary>Free sources start as a thin thread and reach the first node width shortly after.</summary>
    public bool TaperSource { get; init; } = true;

    /// <summary>Length in world units over which a free mouth dissolves into the sea; 0 keeps a round end.</summary>
    public double MouthFade { get; init; }

    /// <summary>Hand-drawn wobble of the banks, 0..1 of the local half width.</summary>
    public double EdgeRoughness { get; init; }

    /// <summary>The water carves the Land layers below: the coast wraps the banks and the sea enters the mouth.</summary>
    public bool CutsLand { get; init; } = true;

    /// <summary>
    /// Draw the water only over Land coverage, so a river ends exactly at the coast and the sea
    /// enters the carved mouth. Ignored on a map that has no land coverage at all.
    /// </summary>
    public bool ClipToLand { get; init; } = true;
}

/// <summary>
/// One connected water system: channels, distributaries and lakes with one shared appearance.
/// Saved inputs only: outlines, junctions and every render cache are derived and recomputed locally.
/// </summary>
public sealed record River
{
    public const double MinimumWidth = 0.25;
    public const double MaximumWidth = 100_000;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Река";
    public ImmutableArray<RiverChannel> Channels { get; init; } = [];
    public RiverStyle Style { get; init; } = new();

    /// <summary>
    /// Per-river visibility and lock, the same two switches every other object of the map has.
    /// They belong to the river and not to its layer, so one water system can be hidden or
    /// protected without hiding the layer it shares with objects. Absent in older files, where
    /// the defaults restore exactly the previous behaviour.
    /// </summary>
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }

    /// <summary>Multiplies every node width, so a whole river can be made wider without retouching nodes.</summary>
    public double WidthScale { get; init; } = 1;

    public RiverChannel? FindChannel(Guid id) => Channels.FirstOrDefault(c => c.Id == id);

    /// <summary>Channels whose mouth or source lies on the given channel.</summary>
    public IEnumerable<RiverChannel> AttachedTo(Guid channelId) =>
        Channels.Where(c => c.Mouth?.ParentChannelId == channelId || c.Source?.ParentChannelId == channelId);

    /// <summary>The channels this channel is attached to, directly (its mouth and source parents).</summary>
    public IEnumerable<Guid> ParentsOf(Guid channelId)
    {
        var channel = FindChannel(channelId);
        if (channel?.Mouth is { } mouth) yield return mouth.ParentChannelId;
        if (channel?.Source is { } source) yield return source.ParentChannelId;
    }

    /// <summary>True when <paramref name="targetId"/> is the channel itself or a channel it is (transitively) attached to.</summary>
    public bool DependsOn(Guid channelId, Guid targetId)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>(); stack.Push(channelId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == targetId) return true;
            if (!visited.Add(current)) continue;
            foreach (var parent in ParentsOf(current)) stack.Push(parent);
        }
        return false;
    }

    /// <summary>Kept for the mouth chain: true when target is the channel or lies downstream of it.</summary>
    public bool FlowsInto(Guid channelId, Guid targetId) => DependsOn(channelId, targetId);

    public River WithNewIds()
    {
        var channelIds = Channels.ToDictionary(c => c.Id, _ => Guid.NewGuid());
        var nodeIds = Channels.SelectMany(c => c.Nodes).ToDictionary(n => n.Id, _ => Guid.NewGuid());
        RiverJunction? Map(RiverJunction? j) => j is null ? null
            : j with { ParentChannelId = channelIds[j.ParentChannelId], ParentNodeId = nodeIds[j.ParentNodeId] };
        return this with
        {
            Id = Guid.NewGuid(),
            Channels = [.. Channels.Select(c => c with
            {
                Id = channelIds[c.Id],
                Nodes = [.. c.Nodes.Select(n => n with { Id = nodeIds[n.Id] })],
                Mouth = Map(c.Mouth),
                Source = Map(c.Source),
            })],
        };
    }

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Channels.IsDefault || Channels.IsEmpty
            || Style is null || !double.IsFinite(WidthScale) || WidthScale is < 0.01 or > 100)
            throw new ArgumentException("Некорректные параметры реки.");
        if (Channels.Any(c => c is null) || Channels.Select(c => c.Id).Distinct().Count() != Channels.Length)
            throw new ArgumentException("Русла реки должны иметь уникальные идентификаторы.");
        var nodeIds = new HashSet<Guid>();
        foreach (var channel in Channels)
        {
            if (channel.Id == Guid.Empty || string.IsNullOrWhiteSpace(channel.Name) || channel.Nodes.IsDefault
                || channel.Nodes.Length < (channel.Closed ? 3 : 2))
                throw new ArgumentException(channel.Closed ? "У озера должно быть хотя бы три узла." : "У русла должно быть хотя бы два узла.");
            foreach (var node in channel.Nodes)
            {
                if (node is null || node.Id == Guid.Empty || !nodeIds.Add(node.Id)
                    || !double.IsFinite(node.X) || !double.IsFinite(node.Y)
                    || !double.IsFinite(node.Width) || node.Width is < MinimumWidth or > MaximumWidth
                    || !double.IsFinite(node.Tension)
                    || node.Tension < RiverNode.MinimumTension || node.Tension > RiverNode.MaximumTension)
                    throw new ArgumentException("Некорректный узел русла.");
            }
            if (channel.Closed && (channel.Mouth is not null || channel.Source is not null))
                throw new ArgumentException("Озеро не впадает и не вытекает само: присоедините к нему русло.");
            ValidateJunction(channel, channel.Mouth, "Устье");
            ValidateJunction(channel, channel.Source, "Исток");
        }
        foreach (var channel in Channels)
            foreach (var parent in ParentsOf(channel.Id))
                if (DependsOn(parent, channel.Id))
                    throw new ArgumentException("Русло не может впадать в собственное течение или вытекать из него.");
        var s = Style;
        if (string.IsNullOrWhiteSpace(s.WaterColor) || string.IsNullOrWhiteSpace(s.BankColor)
            || string.IsNullOrWhiteSpace(s.HighlightColor)
            || new[] { s.Opacity, s.BankWidth, s.BankShadowOpacity, s.BankShadowBlur, s.HighlightOpacity, s.MouthFade, s.EdgeRoughness }
                .Any(v => !double.IsFinite(v))
            || s.Opacity is < 0 or > 1 || s.BankWidth is < 0 or > 64 || s.BankShadowOpacity is < 0 or > 1
            || s.BankShadowBlur is < 0 or > 64 || s.HighlightOpacity is < 0 or > 1 || s.MouthFade is < 0 or > MaximumWidth
            || s.EdgeRoughness is < 0 or > 1)
            throw new ArgumentException("Некорректное оформление реки.");
    }

    private void ValidateJunction(RiverChannel channel, RiverJunction? junction, string what)
    {
        if (junction is null) return;
        var parent = FindChannel(junction.ParentChannelId);
        if (parent is null || parent.Id == channel.Id || !double.IsFinite(junction.Fraction) || junction.Fraction is < 0 or > 1)
            throw new ArgumentException($"{what} указывает на несуществующее русло.");
        var segment = parent.Nodes.ToList().FindIndex(n => n.Id == junction.ParentNodeId);
        var lastSegment = parent.Closed ? parent.Nodes.Length - 1 : parent.Nodes.Length - 2;
        if (segment < 0 || segment > lastSegment)
            throw new ArgumentException($"{what} указывает на несуществующий сегмент русла.");
    }
}
