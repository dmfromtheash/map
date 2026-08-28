using System.Text.Json;

namespace Map.Persistence;

/// <summary>
/// Streaming semantic admission pass for JSON entries. ZIP byte limits alone do not bound the
/// much larger managed object graph that <see cref="JsonSerializer"/> can materialize from tiny
/// tokens such as <c>{}</c>, so every known collection is counted before DTO allocation.
/// </summary>
public static class MapProjectJsonBudget
{
    public const int MaxLayers = 4_096;
    public const int MaxMaterializedObjects = 100_000;
    public const int MaxNotes = 10_000;
    public const int MaxAssets = MapProjectFormat.MaxEntries;
    public const int MaxVectorNodes = 500_000;
    public const int MaxRegions = 100_000;
    public const int MaxPortals = 100_000;
    public const int MaxTiles = 65_536;
    public const int MaxGrids = 16_384;
    public const int MaxFilters = 16_384;
    public const int MaxGenericArrayItems = 100_000;
    public const int MaxStringUtf8Bytes = 1 * 1024 * 1024;
    public const long MaxTokens = 8_000_000;

    private enum AggregateKind
    {
        None,
        Layers,
        Objects,
        Notes,
        Assets,
        Nodes,
        Regions,
        Portals,
        Tiles,
        Grids,
        Filters,
    }

    private sealed class ArrayFrame(
        string label, int elementDepth, int localLimit, AggregateKind aggregateKind)
    {
        public string Label { get; } = label;
        public int ElementDepth { get; } = elementDepth;
        public int LocalLimit { get; } = localLimit;
        public AggregateKind AggregateKind { get; } = aggregateKind;
        public int Count { get; set; }
    }

    public static void Validate(ReadOnlySpan<byte> utf8Json, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var arrays = new Stack<ArrayFrame>();
        var aggregates = new long[Enum.GetValues<AggregateKind>().Length];
        string? pendingProperty = null;
        var pendingPropertyDepth = -1;
        long tokens = 0;

        while (reader.Read())
        {
            tokens++;
            if (tokens > MaxTokens)
            {
                ThrowLimit(entryName, $"more than {MaxTokens} JSON tokens");
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (ValueLength(reader) > 256)
                {
                    ThrowLimit(entryName, "a JSON property name longer than 256 UTF-8 bytes");
                }

                pendingProperty = reader.GetString();
                pendingPropertyDepth = reader.CurrentDepth;
                continue;
            }

            if (arrays.TryPeek(out var current)
                && reader.CurrentDepth == current.ElementDepth
                && StartsValue(reader.TokenType))
            {
                current.Count++;
                if (current.Count > current.LocalLimit)
                {
                    ThrowLimit(
                        entryName,
                        $"more than {current.LocalLimit} items in the \"{current.Label}\" array");
                }

                if (current.AggregateKind != AggregateKind.None)
                {
                    var aggregate = ++aggregates[(int)current.AggregateKind];
                    var aggregateLimit = LimitFor(current.AggregateKind);
                    if (aggregate > aggregateLimit)
                    {
                        ThrowLimit(
                            entryName,
                            $"more than {aggregateLimit} total {LabelFor(current.AggregateKind)}");
                    }
                }
            }

            if (reader.TokenType == JsonTokenType.String
                && ValueLength(reader) > MaxStringUtf8Bytes)
            {
                ThrowLimit(
                    entryName,
                    $"a string longer than {MaxStringUtf8Bytes} UTF-8 bytes");
            }

            var propertyForValue = pendingPropertyDepth == reader.CurrentDepth
                ? pendingProperty
                : null;

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var (label, limit, aggregateKind) = ArrayLimit(propertyForValue);
                arrays.Push(new ArrayFrame(label, reader.CurrentDepth + 1, limit, aggregateKind));
            }
            else if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (arrays.Count > 0)
                {
                    arrays.Pop();
                }
            }

            if (propertyForValue is not null && StartsValue(reader.TokenType))
            {
                pendingProperty = null;
                pendingPropertyDepth = -1;
            }
        }
    }

    private static (string Label, int Limit, AggregateKind AggregateKind) ArrayLimit(
        string? propertyName) => propertyName switch
        {
            "layers" => (propertyName, MaxLayers, AggregateKind.Layers),
            "objects" or "items" => (propertyName, MaxMaterializedObjects, AggregateKind.Objects),
            "notes" => (propertyName, MaxNotes, AggregateKind.Notes),
            "assets" => (propertyName, MaxAssets, AggregateKind.Assets),
            "nodes" => (propertyName, MaxVectorNodes, AggregateKind.Nodes),
            "regions" => (propertyName, MaxRegions, AggregateKind.Regions),
            "portals" => (propertyName, MaxPortals, AggregateKind.Portals),
            "tiles" => (propertyName, MaxTiles, AggregateKind.Tiles),
            "grids" => (propertyName, MaxGrids, AggregateKind.Grids),
            "filters" => (propertyName, MaxFilters, AggregateKind.Filters),
            _ => (propertyName ?? "anonymous", MaxGenericArrayItems, AggregateKind.None),
        };

    private static int LimitFor(AggregateKind kind) => kind switch
    {
        AggregateKind.Layers => MaxLayers,
        AggregateKind.Objects => MaxMaterializedObjects,
        AggregateKind.Notes => MaxNotes,
        AggregateKind.Assets => MaxAssets,
        AggregateKind.Nodes => MaxVectorNodes,
        AggregateKind.Regions => MaxRegions,
        AggregateKind.Portals => MaxPortals,
        AggregateKind.Tiles => MaxTiles,
        AggregateKind.Grids => MaxGrids,
        AggregateKind.Filters => MaxFilters,
        _ => int.MaxValue,
    };

    private static string LabelFor(AggregateKind kind) => kind switch
    {
        AggregateKind.Objects => "scene/composition objects",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static bool StartsValue(JsonTokenType tokenType) => tokenType is
        JsonTokenType.StartObject
        or JsonTokenType.StartArray
        or JsonTokenType.String
        or JsonTokenType.Number
        or JsonTokenType.True
        or JsonTokenType.False
        or JsonTokenType.Null;

    private static long ValueLength(Utf8JsonReader reader) => reader.HasValueSequence
        ? reader.ValueSequence.Length
        : reader.ValueSpan.Length;

    private static void ThrowLimit(string entryName, string detail) =>
        throw new MapProjectResourceLimitException(
            $"Entry {entryName} contains {detail}, outside this build's semantic JSON budget. " +
            "The file was not modified.");
}
