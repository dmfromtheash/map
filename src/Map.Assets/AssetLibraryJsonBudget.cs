using System.Text.Json;

namespace Map.Assets;

/// <summary>
/// Streaming admission for catalog.json. The catalog is persistent same-user state, but it can be
/// truncated, hand-edited or replaced; byte and semantic limits must apply before JsonSerializer
/// materializes its lists and dictionaries during application startup.
/// </summary>
public static class AssetLibraryJsonBudget
{
    public const long MaxEncodedBytes = 32L * 1024 * 1024;
    public const int MaxRecords = 50_000;
    public const int MaxVariants = 50_000;
    public const int MaxSets = 50_000;
    public const int MaxRecent = AssetLibraryDocument.MaxRecent;
    public const int MaxTags = 1_000_000;
    public const int MaxVariantIds = 1_000_000;
    public const int MaxCurrentVariants = MaxSets;
    public const int MaxStringUtf8Bytes = 1 * 1024 * 1024;
    public const long MaxTokens = 4_000_000;

    private sealed class ArrayFrame(string label, int elementDepth, int localLimit)
    {
        public string Label { get; } = label;
        public int ElementDepth { get; } = elementDepth;
        public int LocalLimit { get; } = localLimit;
        public int Count { get; set; }
    }

    public static JsonValueKind Validate(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxEncodedBytes)
        {
            throw new InvalidDataException(
                $"catalog.json exceeds the {MaxEncodedBytes}-byte startup budget.");
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var arrays = new Stack<ArrayFrame>();
        var aggregateArrayCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var currentVariantMaps = new Stack<int>();
        string? pendingProperty = null;
        var pendingPropertyDepth = -1;
        long currentVariantCount = 0;
        long tags = 0;
        long variantIds = 0;
        long tokens = 0;
        var root = JsonValueKind.Undefined;

        while (reader.Read())
        {
            tokens++;
            if (tokens > MaxTokens)
            {
                ThrowLimit($"more than {MaxTokens} JSON tokens");
            }

            if (root == JsonValueKind.Undefined)
            {
                root = reader.TokenType switch
                {
                    JsonTokenType.StartArray => JsonValueKind.Array,
                    JsonTokenType.StartObject => JsonValueKind.Object,
                    _ => throw new InvalidDataException("catalog.json root must be an object or array."),
                };
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (ValueLength(reader) > 256)
                {
                    ThrowLimit("a property name longer than 256 UTF-8 bytes");
                }

                if (currentVariantMaps.TryPeek(out var mapDepth)
                    && reader.CurrentDepth == mapDepth)
                {
                    currentVariantCount++;
                    if (currentVariantCount > MaxCurrentVariants)
                    {
                        ThrowLimit($"more than {MaxCurrentVariants} current-variant entries");
                    }
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
                    ThrowLimit($"more than {current.LocalLimit} items in the \"{current.Label}\" array");
                }

                var aggregate = aggregateArrayCounts.GetValueOrDefault(current.Label) + 1;
                aggregateArrayCounts[current.Label] = aggregate;
                if (aggregate > current.LocalLimit)
                {
                    ThrowLimit(
                        $"more than {current.LocalLimit} aggregate items in \"{current.Label}\" arrays");
                }

                if (current.Label == "tags" && ++tags > MaxTags)
                {
                    ThrowLimit($"more than {MaxTags} total tags");
                }

                if (current.Label == "variantIds" && ++variantIds > MaxVariantIds)
                {
                    ThrowLimit($"more than {MaxVariantIds} total variant ids");
                }
            }

            if (reader.TokenType == JsonTokenType.String && ValueLength(reader) > MaxStringUtf8Bytes)
            {
                ThrowLimit($"a string longer than {MaxStringUtf8Bytes} UTF-8 bytes");
            }

            var propertyForValue = pendingPropertyDepth == reader.CurrentDepth
                ? pendingProperty
                : null;
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var (label, limit) = ArrayLimit(propertyForValue, reader.CurrentDepth, root);
                arrays.Push(new ArrayFrame(label, reader.CurrentDepth + 1, limit));
            }
            else if (reader.TokenType == JsonTokenType.EndArray && arrays.Count > 0)
            {
                arrays.Pop();
            }
            else if (reader.TokenType == JsonTokenType.StartObject
                && IsProperty(propertyForValue, "currentVariantBySet"))
            {
                currentVariantMaps.Push(reader.CurrentDepth + 1);
            }
            else if (reader.TokenType == JsonTokenType.EndObject
                && currentVariantMaps.TryPeek(out var closingMapDepth)
                && reader.CurrentDepth + 1 == closingMapDepth)
            {
                currentVariantMaps.Pop();
            }

            if (propertyForValue is not null && StartsValue(reader.TokenType))
            {
                pendingProperty = null;
                pendingPropertyDepth = -1;
            }
        }

        return root;
    }

    private static (string Label, int Limit) ArrayLimit(
        string? propertyName,
        int depth,
        JsonValueKind root)
    {
        if (IsProperty(propertyName, "records")) return ("records", MaxRecords);
        if (IsProperty(propertyName, "collections"))
            return ("collections", AssetLibraryDocument.MaxCollections);
        if (IsProperty(propertyName, "variants")) return ("variants", MaxVariants);
        if (IsProperty(propertyName, "sets")) return ("sets", MaxSets);
        if (IsProperty(propertyName, "recent")) return ("recent", MaxRecent);
        if (IsProperty(propertyName, "tags")) return ("tags", MaxTags);
        if (IsProperty(propertyName, "variantIds")) return ("variantIds", MaxVariantIds);
        return propertyName is null && depth == 0 && root == JsonValueKind.Array
            ? ("records", MaxRecords)
            : ("anonymous", MaxRecords);
    }

    private static bool IsProperty(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

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

    private static void ThrowLimit(string detail) => throw new InvalidDataException(
        $"catalog.json contains {detail}, outside this build's startup budget.");
}
