using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// R27: the brush of the fog of war. A stroke works on a mutable copy of the touched tiles
/// (<see cref="Begin"/>, <see cref="Dab"/>) and becomes one undo step when it ends
/// (<see cref="Finish"/>, <see cref="SetFogCommand"/>).
/// </summary>
public static class MapFogEditor
{
    /// <summary>A stroke in progress: the fog it started from and the tiles it changed so far.</summary>
    public sealed class Stroke(MapFog source)
    {
        public MapFog Source { get; } = source;
        internal Dictionary<long, ulong[]> Tiles { get; } = [];
        public int Version { get; internal set; }

        internal ulong[] Tile(long key)
        {
            if (Tiles.TryGetValue(key, out var rows)) return rows;
            rows = new ulong[MapFog.TileCells];
            if (Source.Revealed.TryGetValue(key, out var stored))
            {
                if (stored.Length == 0) Array.Fill(rows, ulong.MaxValue);
                else stored.CopyTo(rows);
            }
            Tiles[key] = rows;
            return rows;
        }

        /// <summary>The fog as it stands mid-stroke (for drawing the stroke live).</summary>
        public MapFog Current => Source with { Revealed = Merge(Source.Revealed, Tiles) };
    }

    public static Stroke Begin(MapFog fog) => new(fog);

    /// <summary>Reveals (or hides) every cell whose middle lies within the circle.</summary>
    public static void Dab(Stroke stroke, double x, double y, double radius, bool reveal, double canvasWidth, double canvasHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !(radius > 0)) return;
        var maxX = (int)Math.Ceiling(canvasWidth / MapFog.Cell) - 1;
        var maxY = (int)Math.Ceiling(canvasHeight / MapFog.Cell) - 1;
        var left = Math.Max(0, (int)Math.Floor((x - radius) / MapFog.Cell));
        var right = Math.Min(maxX, (int)Math.Floor((x + radius) / MapFog.Cell));
        var top = Math.Max(0, (int)Math.Floor((y - radius) / MapFog.Cell));
        var bottom = Math.Min(maxY, (int)Math.Floor((y + radius) / MapFog.Cell));
        var r2 = radius * radius;
        for (var cy = top; cy <= bottom; cy++)
        {
            var dy = ((cy + .5) * MapFog.Cell) - y;
            if (dy * dy > r2) continue;
            var half = Math.Sqrt(r2 - (dy * dy));
            var from = Math.Max(left, (int)Math.Ceiling(((x - half) / MapFog.Cell) - .5));
            var to = Math.Min(right, (int)Math.Floor(((x + half) / MapFog.Cell) - .5));
            for (var cx = from; cx <= to; cx++)
            {
                var rows = stroke.Tile(MapFog.Key(cx / MapFog.TileCells, cy / MapFog.TileCells));
                var bit = 1UL << (cx % MapFog.TileCells);
                if (reveal) rows[cy % MapFog.TileCells] |= bit;
                else rows[cy % MapFog.TileCells] &= ~bit;
            }
        }
        stroke.Version++;
    }

    /// <summary>The fog after the stroke: full tiles kept as empty row lists, empty tiles dropped.</summary>
    public static MapFog Finish(Stroke stroke) => stroke.Current;

    /// <summary>Every cell of the canvas revealed.</summary>
    public static MapFog RevealAll(MapFog fog, double canvasWidth, double canvasHeight)
    {
        var builder = ImmutableDictionary.CreateBuilder<long, ImmutableArray<ulong>>();
        var tilesX = (int)Math.Ceiling(canvasWidth / MapFog.TileWorld);
        var tilesY = (int)Math.Ceiling(canvasHeight / MapFog.TileWorld);
        if ((long)tilesX * tilesY > MapFog.MaxTiles) throw new InvalidOperationException("Карта слишком велика для тумана.");
        for (var ty = 0; ty < tilesY; ty++)
        for (var tx = 0; tx < tilesX; tx++)
            builder[MapFog.Key(tx, ty)] = [];
        return fog with { Revealed = builder.ToImmutable() };
    }

    public static MapFog HideAll(MapFog fog) => fog with { Revealed = ImmutableDictionary<long, ImmutableArray<ulong>>.Empty };

    private static ImmutableDictionary<long, ImmutableArray<ulong>> Merge(ImmutableDictionary<long, ImmutableArray<ulong>> stored, Dictionary<long, ulong[]> changed)
    {
        if (changed.Count == 0) return stored;
        var builder = stored.ToBuilder();
        foreach (var (key, rows) in changed)
        {
            if (rows.All(r => r == 0)) builder.Remove(key);
            else if (rows.All(r => r == ulong.MaxValue)) builder[key] = [];
            else builder[key] = [.. rows];
        }
        return builder.ToImmutable();
    }
}

/// <summary>The fog of war replaced as one undo step (R27).</summary>
public sealed class SetFogCommand(MapFog before, MapFog after, string name) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        after.Validate();
        return document with { Fog = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Fog = before };
}

/// <summary>
/// R27: what the players may receive of a painted map with fog - only the labels and notes they
/// have seen, only the cards meant for them and not hidden by the fog (DEC-077), no atlas paths.
/// The hex part is sanitized by <see cref="ExportPlayerVisibility"/>.
/// </summary>
public static class PlayerScene
{
    /// <param name="sanitizeHex">False for the live players' window, whose renderer hides the hex secrets itself (and keeps its hex caches).</param>
    public static MapDocument Sanitize(MapDocument document, bool sanitizeHex = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fog = document.Fog;
        var layers = document.Layers.Select(layer => layer switch
        {
            HexLayer hex => sanitizeHex ? ExportPlayerVisibility.Sanitize(hex) : hex,
            ObjectLayer objects when fog.Enabled && objects.Objects.Any(o => o.Kind == SceneObjectKind.Text && !Seen(fog, o)) =>
                objects with { Objects = [.. objects.Objects.Where(o => o.Kind != SceneObjectKind.Text || Seen(fog, o))] },
            _ => layer,
        }).ToArray();
        var notes = document.Notes.Where(n => n.PlayerVisible && (n.AnchorKind != NoteAnchorKind.World || fog.Sees(n.WorldX, n.WorldY))).ToArray();
        var objects = new Lazy<Dictionary<Guid, SceneObject>>(() => ObjectsById(document));
        return new MapDocument(document.Id, document.Title, document.CreatedUtc, document.Canvas, layers, document.Assets, notes, [], document.StampForeground, document.Category)
        {
            Lighting = document.Lighting,
            Style = document.Style,
            Labels = document.Labels,
            Fog = fog,
            Entities = [.. document.Entities.Where(e => e.PlayerVisible && !(fog.Enabled && HiddenByFog(document, e, objects)))],
            Transitions = [.. document.Transitions.Where(t => fog.Sees(t.From.X, t.From.Y))],
            Timeline = document.Timeline,
            Planet = document.Planet,
            Isometric = document.Isometric,
            // The players' pointcrawl: what they have seen, no hidden ways, no master's notes.
            Pointcrawl = PlayersPointcrawl(document.Pointcrawl),
        };
    }

    private static MapPointcrawl PlayersPointcrawl(MapPointcrawl crawl)
    {
        var (nodes, edges) = crawl.ForPlayers();
        return crawl with
        {
            Nodes = [.. nodes.Select(p => p.Known ? p.Node with { Note = "" } : p.Node with { Name = "?", Note = "", EntityId = null })],
            Edges = [.. edges],
            FogByVisit = false,
        };
    }

    private static bool Seen(MapFog fog, SceneObject label) => fog.Sees(label.X + (label.Width / 2), label.Y + (label.Height / 2));

    /// <summary>
    /// DEC-077: the card has places on this map and the players have seen none of them yet. A bound object or note
    /// is looked up where it stands now; a river, road, area or bare point by the point kept with the binding.
    /// A hex binding holds a cell, not a point, and is left to the hex visibility; a card with no place on this map
    /// is never hidden by its fog.
    /// </summary>
    public static bool HiddenByFog(MapDocument document, MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entity);
        return document.Fog.Enabled && HiddenByFog(document, entity, new Lazy<Dictionary<Guid, SceneObject>>(() => ObjectsById(document)));
    }

    /// <summary>The cards with «Видно игрокам» that the fog keeps from the players (the line of the export for players).</summary>
    public static int HiddenByFogCount(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Fog.Enabled) return 0;
        var objects = new Lazy<Dictionary<Guid, SceneObject>>(() => ObjectsById(document));
        return document.Entities.Count(e => e.PlayerVisible && HiddenByFog(document, e, objects));
    }

    private static bool HiddenByFog(MapDocument document, MapEntity entity, Lazy<Dictionary<Guid, SceneObject>> objects)
    {
        var places = entity.Bindings.Where(b => (b.MapId is null || b.MapId == document.Id.Value) && b.Kind != EntityTargetKind.Hex).ToList();
        return places.Count > 0 && !places.Any(b => PlaceSeen(document, objects, b));
    }

    private static Dictionary<Guid, SceneObject> ObjectsById(MapDocument document)
    {
        var result = new Dictionary<Guid, SceneObject>();
        foreach (var item in document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)) result.TryAdd(item.Id.Value, item);
        return result;
    }

    private static bool PlaceSeen(MapDocument document, Lazy<Dictionary<Guid, SceneObject>> objects, EntityBinding binding)
    {
        var fog = document.Fog;
        if (binding.Kind == EntityTargetKind.Object && objects.Value.TryGetValue(binding.TargetId, out var item))
            return Seen(fog, item);
        if (binding.Kind == EntityTargetKind.Note && document.Notes.FirstOrDefault(n => n.Id.Value == binding.TargetId) is { AnchorKind: NoteAnchorKind.World } note)
            return fog.Sees(note.WorldX, note.WorldY);
        return fog.Sees(binding.X, binding.Y);
    }
}
