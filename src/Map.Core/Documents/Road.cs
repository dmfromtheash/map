using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// One shared node of a road network, in world units. A node belongs to the road, not to a
/// branch: several branches may pass through or end at the same node, which is what makes a
/// fork one editable thing. Moving the node moves every branch that uses it.
/// </summary>
public sealed record RoadNode(Guid Id, double X, double Y);

/// <summary>
/// One vertex of a branch: the shared node it passes through and how this branch looks there.
/// <see cref="Width"/> is the full width of this branch at the node; between vertices it is
/// interpolated along the branch length. Two branches meeting at one node each keep their own
/// width there, so a wide road and a narrow path can share a junction.
/// <see cref="Tension"/> scales the tangent the smooth course leaves and enters the node with:
/// 1 is the plain curve, 0 a corner (what <see cref="Sharp"/> forces), above 1 a wider swing.
/// </summary>
public sealed record RoadVertex(Guid NodeId, double Width, bool Sharp = false, double Tension = 1)
{
    public const double MinimumTension = 0;
    public const double MaximumTension = 2.5;

    public double EffectiveTension => Sharp ? 0 : Math.Clamp(Tension, MinimumTension, MaximumTension);
}

/// <summary>How a free end of a branch is finished.</summary>
public enum RoadEndCap
{
    /// <summary>A plain end across the road.</summary>
    Flat,

    /// <summary>A rounded end.</summary>
    Round,

    /// <summary>A path that narrows and dissolves into the ground.</summary>
    Taper,
}

/// <summary>
/// The way a road is drawn - not only its colours. Each way is a different set of passes:
/// what the shoulder is, whether the edge is a pen line or a tonal step, whether ruts are drawn.
/// </summary>
public enum RoadDrawing
{
    /// <summary>Warm earth, a soft uneven shoulder, a quiet edge and restrained ruts.</summary>
    Country,

    /// <summary>Expressive broken pen strokes of varying weight, a light body and visible ruts.</summary>
    PenRuts,

    /// <summary>Broad soft changes of shape, a delicate tonal step to the grass, hardly any contour.</summary>
    Painterly,
}

/// <summary>
/// Appearance of a road or of one branch of it. Evaluated at render time from the geometry and
/// the seed; never baked. Every colour is "#AARRGGBB".
/// </summary>
public sealed record RoadStyle
{
    public RoadDrawing Drawing { get; init; } = RoadDrawing.Country;

    /// <summary>The body of the road.</summary>
    public string SurfaceColor { get; init; } = "#FFDCC59F";

    /// <summary>A second earth tone laid in soft patches along the body; 0 variation keeps one flat tone.</summary>
    public string ToneColor { get; init; } = "#FFC4A77B";
    public double ToneVariation { get; init; } = 0.4;

    public double Opacity { get; init; } = 1;

    /// <summary>Soft earth transition outside the body, in world units; 0 removes it.</summary>
    public string ShoulderColor { get; init; } = "#FFB79A6C";
    public double ShoulderWidth { get; init; } = 5;
    public double ShoulderOpacity { get; init; } = 0.42;

    /// <summary>The drawn edge of the road: a thin broken line, or a tonal step in the painterly drawing.</summary>
    public string EdgeColor { get; init; } = "#FF5E4B34";
    public double EdgeWidth { get; init; } = 1.1;
    public double EdgeOpacity { get; init; } = 0.62;

    /// <summary>How much of the edge line is left out, 0..1: 0 is continuous, 1 mostly gaps.</summary>
    public double EdgeBreak { get; init; } = 0.28;

    /// <summary>Wheel ruts along the course; 0 opacity removes them.</summary>
    public string RutColor { get; init; } = "#FF8A6F4E";
    public double RutOpacity { get; init; } = 0.32;

    /// <summary>Small marks: pebbles by a country road, ticks by a pen road. 0 removes them.</summary>
    public double DetailOpacity { get; init; } = 0.35;

    /// <summary>Independent slow variation of the left and right edge, 0..1 of the local half width.</summary>
    public double EdgeRoughness { get; init; } = 0.3;

    public RoadEndCap StartCap { get; init; } = RoadEndCap.Round;
    public RoadEndCap EndCap { get; init; } = RoadEndCap.Round;

    /// <summary>Length of a tapered end in world units; 0 derives it from the width.</summary>
    public double TaperLength { get; init; }

    /// <summary>A built-in ground material over the body (<see cref="MapFillTextures"/>); null keeps the flat tone.</summary>
    public string? TextureId { get; init; }
    public double TextureOpacity { get; init; } = 0.45;
    public double TextureScale { get; init; } = MapFillTextures.DefaultGroundScale;

    /// <summary>
    /// Strength 0..1 of the owner's painted pass (schema v23): form shadow on the side away from a
    /// north-west light, dry-brush strokes of earth along the way and a heavier ink contour on the
    /// shadow side. 0 = none, as in every older map.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public double PaintStrokes { get; init; }

    /// <summary>«Стена блокирует свет»: the road's axis is a wall in the dd2vtt export (a castle wall drawn as a road).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BlocksLight { get; init; }

    /// <summary>«Просёлочная»: U1/U2 - warm earth, uneven soft shoulder, quiet edge, restrained ruts.</summary>
    public static RoadStyle Country() => new();

    /// <summary>«Перо и колея»: U4/E1 - broken strokes of varying weight, a light body, visible ruts.</summary>
    public static RoadStyle PenRuts() => new()
    {
        Drawing = RoadDrawing.PenRuts,
        SurfaceColor = "#FFEBE0C4", ToneColor = "#FFDACB9F", ToneVariation = 0.22,
        ShoulderColor = "#FFB9A27A", ShoulderWidth = 3, ShoulderOpacity = 0.2,
        EdgeColor = "#FF3B3128", EdgeWidth = 1.25, EdgeOpacity = 0.85, EdgeBreak = 0.42,
        RutColor = "#FF4A3E32", RutOpacity = 0.55, DetailOpacity = 0.6, EdgeRoughness = 0.22,
    };

    /// <summary>«Мягкая живописная»: U3/E2 - broad soft shape changes, a delicate tonal step, hardly a contour.</summary>
    public static RoadStyle Painterly() => new()
    {
        Drawing = RoadDrawing.Painterly,
        SurfaceColor = "#FFE6DCBD", ToneColor = "#FFCFC09A", ToneVariation = 0.55,
        ShoulderColor = "#FFA9B27A", ShoulderWidth = 8, ShoulderOpacity = 0.35,
        EdgeColor = "#FF8E7D5A", EdgeWidth = 0, EdgeOpacity = 0.3, EdgeBreak = 0.7,
        RutColor = "#FFB8A57F", RutOpacity = 0.12, DetailOpacity = 0.1, EdgeRoughness = 0.5,
        StartCap = RoadEndCap.Taper, EndCap = RoadEndCap.Taper,
    };

    /// <summary>
    /// «Живописная» (2026-10-22): the road in the manner of the owner's hand-painted mountains - a
    /// pen contour of varying weight in the same slate ink, a muted earth of a limited palette,
    /// visible dry-brush strokes along the way, the form shadow away from the north-west light.
    /// </summary>
    public static RoadStyle Painted() => new()
    {
        Drawing = RoadDrawing.PenRuts,
        SurfaceColor = "#FFD2BC92", ToneColor = "#FFAE9269", ToneVariation = 0.4,
        ShoulderColor = "#FF9C8A66", ShoulderWidth = 2.5, ShoulderOpacity = 0.14,
        EdgeColor = "#FF262A30", EdgeWidth = 0.8, EdgeOpacity = 0.8, EdgeBreak = 0.08,
        RutColor = "#FF6E5A42", RutOpacity = 0.2, DetailOpacity = 0.3, EdgeRoughness = 0.3,
        PaintStrokes = 1,
    };

    public static RoadStyle Preset(RoadDrawing drawing) => drawing switch
    {
        RoadDrawing.PenRuts => PenRuts(),
        RoadDrawing.Painterly => Painterly(),
        _ => Country(),
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Drawing) || !Enum.IsDefined(StartCap) || !Enum.IsDefined(EndCap)
            || new[] { SurfaceColor, ToneColor, ShoulderColor, EdgeColor, RutColor }.Any(string.IsNullOrWhiteSpace)
            || new[] { ToneVariation, Opacity, ShoulderWidth, ShoulderOpacity, EdgeWidth, EdgeOpacity, EdgeBreak, RutOpacity, DetailOpacity, EdgeRoughness, TaperLength, PaintStrokes }
                .Any(v => !double.IsFinite(v))
            || ToneVariation is < 0 or > 1 || Opacity is < 0 or > 1 || ShoulderWidth is < 0 or > 256 || ShoulderOpacity is < 0 or > 1
            || EdgeWidth is < 0 or > 32 || EdgeOpacity is < 0 or > 1 || EdgeBreak is < 0 or > 1 || RutOpacity is < 0 or > 1
            || DetailOpacity is < 0 or > 1 || EdgeRoughness is < 0 or > 1 || TaperLength is < 0 or > Road.MaximumWidth || PaintStrokes is < 0 or > 1
            || !MapFillTextures.IsValidId(TextureId) || !MapFillTextures.IsValidOpacity(TextureOpacity) || !MapFillTextures.IsValidScale(TextureScale))
            throw new ArgumentException("Некорректное оформление дороги.");
    }
}

/// <summary>
/// One stretch of a road: an ordered walk over shared nodes with this branch's own width at each
/// of them. A branch is open, or closed back onto its first node (a loop around a block). It may
/// carry a look of its own; otherwise it is drawn like the road.
/// </summary>
public sealed record RoadBranch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Участок";
    public ImmutableArray<RoadVertex> Vertices { get; init; } = [];
    public bool Closed { get; init; }

    /// <summary>Null: drawn with the road's style.</summary>
    public RoadStyle? Style { get; init; }

    public int IndexOf(Guid nodeId)
    {
        for (var i = 0; i < Vertices.Length; i++) if (Vertices[i].NodeId == nodeId) return i;
        return -1;
    }

    /// <summary>Number of segments the branch has: one fewer than the vertices, or as many when closed.</summary>
    public int SegmentCount => Closed ? Vertices.Length : Math.Max(0, Vertices.Length - 1);
}

/// <summary>
/// One road network: shared nodes, branches walking over them, one style and a seed for the
/// drawn details. Saved inputs only: outlines, forks and every render cache are derived. Unlike
/// a river a road has no flow, may close into a loop, never carves the land and does not widen
/// towards anything - width is simply what the user set at each vertex.
/// </summary>
public sealed record Road
{
    public const double MinimumWidth = 0.25;
    public const double MaximumWidth = 100_000;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Дорога";
    public ImmutableArray<RoadNode> Nodes { get; init; } = [];
    public ImmutableArray<RoadBranch> Branches { get; init; } = [];
    public RoadStyle Style { get; init; } = new();

    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }

    /// <summary>Multiplies every vertex width, so a whole network can be made wider at once.</summary>
    public double WidthScale { get; init; } = 1;

    /// <summary>The road's level among the terrain, like water and land (<see cref="TerrainOrder"/>).</summary>
    public int Level { get; init; }

    /// <summary>Seed of the drawn details, so a saved road looks the same on every open and in every tile.</summary>
    public int Seed { get; init; }

    /// <summary>The user means this road as it is (R24, schema v35): the geography check does not report it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool GeographyIntentional { get; init; }

    /// <summary>The years the road exists (R31, schema v40); null - always.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Era? Era { get; init; }

    /// <summary>Why a generator made this road (R23, schema v31); null for a road drawn by hand.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationReason { get; init; }

    /// <summary>
    /// Kinds chosen by hand for the crossings this road makes over rivers (R06). The crossings
    /// themselves are derived from the geometry (<c>RoadCrossings.Detect</c>); only departures from
    /// the default bridge are stored, so a road without them keeps its older schema.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ImmutableArray<RoadCrossingOverride> CrossingOverrides { get; init; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("crossings")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<RoadCrossingOverride>? CrossingsJson
    {
        get => CrossingOverrides.IsDefaultOrEmpty ? null : [.. CrossingOverrides];
        init => CrossingOverrides = value is null ? [] : [.. value];
    }

    /// <summary>True when the road or one of its branches is drawn in the painted manner (schema v23).</summary>
    public bool NeedsPaintedSchema => Style.PaintStrokes > 0 || Branches.Any(b => b.Style is { PaintStrokes: > 0 });

    /// <summary>True when a crossing kind was chosen by hand (schema v15).</summary>
    public bool NeedsCrossingsSchema => !CrossingOverrides.IsDefaultOrEmpty;

    public RoadNode? FindNode(Guid id) => Nodes.FirstOrDefault(n => n.Id == id);
    public RoadBranch? FindBranch(Guid id) => Branches.FirstOrDefault(b => b.Id == id);

    public RoadStyle EffectiveStyle(RoadBranch branch) => branch.Style ?? Style;

    /// <summary>Branches that use the node, each once even when a closed loop returns to it.</summary>
    public IEnumerable<RoadBranch> BranchesAt(Guid nodeId) => Branches.Where(b => b.IndexOf(nodeId) >= 0);

    /// <summary>
    /// How many branch ends and passes meet at a node: an interior vertex counts two, an end one.
    /// One or two means a plain course, three or more a fork.
    /// </summary>
    public int Degree(Guid nodeId)
    {
        var degree = 0;
        foreach (var branch in Branches)
            for (var i = 0; i < branch.Vertices.Length; i++)
                if (branch.Vertices[i].NodeId == nodeId)
                    degree += branch.Closed || (i > 0 && i < branch.Vertices.Length - 1) ? 2 : 1;
        return degree;
    }

    /// <summary>True when more than one branch uses the node: the place where branches are joined.</summary>
    public bool IsShared(Guid nodeId) => BranchesAt(nodeId).Skip(1).Any();

    public Road WithNewIds()
    {
        var nodeIds = Nodes.ToDictionary(n => n.Id, _ => Guid.NewGuid());
        var branchIds = Branches.ToDictionary(b => b.Id, _ => Guid.NewGuid());
        return this with
        {
            Id = Guid.NewGuid(),
            Nodes = [.. Nodes.Select(n => n with { Id = nodeIds[n.Id] })],
            Branches = [.. Branches.Select(b => b with
            {
                Id = branchIds[b.Id],
                Vertices = [.. b.Vertices.Select(v => v with { NodeId = nodeIds[v.NodeId] })],
            })],
            CrossingOverrides = [.. CrossingOverrides.Where(c => branchIds.ContainsKey(c.BranchId)).Select(c => c with { BranchId = branchIds[c.BranchId] })],
        };
    }

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Nodes.IsDefault || Branches.IsDefault || Branches.IsEmpty
            || Style is null || !double.IsFinite(WidthScale) || WidthScale is < 0.01 or > 100 || !TerrainOrder.IsValidLevel(Level)
            || Era is { IsValid: false })
            throw new ArgumentException("Некорректные параметры дороги.");
        Style.Validate();
        if (!CrossingOverrides.IsDefaultOrEmpty)
        {
            var keys = new HashSet<(Guid, Guid, int)>();
            foreach (var crossing in CrossingOverrides)
                if (crossing is null || crossing.Ordinal < 0 || !Enum.IsDefined(crossing.Kind)
                    || FindBranch(crossing.BranchId) is null || !keys.Add((crossing.BranchId, crossing.ChannelId, crossing.Ordinal)))
                    throw new ArgumentException("Некорректная переправа дороги.");
        }
        var nodeIds = new HashSet<Guid>();
        foreach (var node in Nodes)
        {
            if (node is null || node.Id == Guid.Empty || !nodeIds.Add(node.Id) || !double.IsFinite(node.X) || !double.IsFinite(node.Y))
                throw new ArgumentException("Некорректный узел дороги.");
        }
        if (Branches.Any(b => b is null) || Branches.Select(b => b.Id).Distinct().Count() != Branches.Length)
            throw new ArgumentException("Участки дороги должны иметь уникальные идентификаторы.");
        var used = new HashSet<Guid>();
        foreach (var branch in Branches)
        {
            if (branch.Id == Guid.Empty || string.IsNullOrWhiteSpace(branch.Name) || branch.Vertices.IsDefault
                || branch.Vertices.Length < (branch.Closed ? 3 : 2))
                throw new ArgumentException(branch.Closed ? "У замкнутого участка должно быть хотя бы три узла." : "У участка должно быть хотя бы два узла.");
            branch.Style?.Validate();
            for (var i = 0; i < branch.Vertices.Length; i++)
            {
                var vertex = branch.Vertices[i];
                if (vertex is null || !nodeIds.Contains(vertex.NodeId)
                    || !double.IsFinite(vertex.Width) || vertex.Width is < MinimumWidth or > MaximumWidth
                    || !double.IsFinite(vertex.Tension) || vertex.Tension < RoadVertex.MinimumTension || vertex.Tension > RoadVertex.MaximumTension)
                    throw new ArgumentException("Некорректная вершина участка дороги.");
                if (i > 0 && branch.Vertices[i - 1].NodeId == vertex.NodeId)
                    throw new ArgumentException("Участок дважды подряд проходит через один узел.");
                used.Add(vertex.NodeId);
            }
            if (branch.Closed && branch.Vertices[0].NodeId == branch.Vertices[^1].NodeId)
                throw new ArgumentException("Замкнутый участок не повторяет первый узел в конце.");
        }
        if (used.Count != nodeIds.Count)
            throw new ArgumentException("У дороги есть узел, через который не проходит ни один участок.");
    }
}
