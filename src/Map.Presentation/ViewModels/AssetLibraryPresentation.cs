using System.Text;
using Map.Assets;
using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// What the library knows about one content file right now. Missing and changed content must be
/// shown apart from a healthy card, and apart from "not checked yet": a card that silently looks
/// available while its original is gone is the failure this state exists to prevent.
/// </summary>
public enum AssetAvailability
{
    /// <summary>Not verified yet; nothing has been read from disk for this content.</summary>
    Unknown = 0,

    /// <summary>A verification is in flight; the answer replaces this state when it lands.</summary>
    Checking = 1,

    Available = 2,

    /// <summary>The stored or linked original is not where the catalog says it is.</summary>
    Missing = 3,

    /// <summary>The file exists but no longer hashes to the content the catalog recorded.</summary>
    HashMismatch = 4,

    /// <summary>The file is there and unchanged, but its format is not one we can draw.</summary>
    Unsupported = 5,
}

/// <summary>One resolved original: the reference a command can place, or why it cannot.</summary>
public sealed record AssetContentResolution(AssetAvailability Availability, MapAssetReference? Reference)
{
    public static AssetContentResolution Failed(AssetAvailability availability) => new(availability, null);

    public bool IsAvailable => Availability == AssetAvailability.Available && Reference is not null;
}

/// <summary>
/// Turns catalog content into something a gesture can commit. It exists as an interface so the
/// cost of a gesture is observable: a test can count exactly how many originals were opened.
/// </summary>
public interface IAssetContentResolver
{
    /// <summary>Verifies and reads one original. Called at most once per distinct hash.</summary>
    AssetContentResolution Resolve(AssetRecord record);

    /// <summary>Verifies one original without carrying its payload into memory.</summary>
    AssetAvailability Check(AssetRecord record);
}

/// <summary>The real resolver: hash verification and payload both come from the local catalog.</summary>
public sealed class CatalogAssetContentResolver(AssetCatalog catalog) : IAssetContentResolver
{
    private static readonly string[] Supported = ["image/png", "image/jpeg", "image/webp"];

    public AssetAvailability Check(AssetRecord record) => Verify(record).Availability;

    public AssetContentResolution Resolve(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var (availability, path) = Verify(record);
        if (availability != AssetAvailability.Available || path is null)
        {
            return AssetContentResolution.Failed(availability);
        }

        try
        {
            var thumbnailPath = catalog.ThumbnailPath(record);
            return new AssetContentResolution(
                AssetAvailability.Available,
                new MapAssetReference(
                    record.Sha256, record.DisplayName, record.MediaType, record.Extension,
                    ProjectAssetStorage.Embedded, record.ImportedUtc,
                    record.Provenance.Source, record.Provenance.LicenseId,
                    embeddedContent: File.ReadAllBytes(path),
                    fallbackThumbnailPng: File.Exists(thumbnailPath) ? File.ReadAllBytes(thumbnailPath) : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file passed verification and then went away or locked: still not placeable.
            return AssetContentResolution.Failed(AssetAvailability.Missing);
        }
    }

    private (AssetAvailability Availability, string? Path) Verify(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Supported.Contains(record.MediaType, StringComparer.OrdinalIgnoreCase))
        {
            return (AssetAvailability.Unsupported, null);
        }

        try
        {
            var resolution = catalog.Resolve(record);
            return resolution.Status switch
            {
                AssetResolutionStatus.Available => (AssetAvailability.Available, resolution.Path),
                AssetResolutionStatus.HashMismatch => (AssetAvailability.HashMismatch, resolution.Path),
                _ => (AssetAvailability.Missing, resolution.Path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (AssetAvailability.Missing, null);
        }
        catch (InvalidDataException)
        {
            return (AssetAvailability.Unsupported, null);
        }
    }
}

public enum AssetLibraryRowKind
{
    Header = 0,
    Card = 1,
}

/// <summary>
/// One row of the library surface. Grouping does not build a second control tree: headers and
/// cards live in the same flat sequence, so one virtualized list realises only what is on screen
/// whether the user groups by collection, by tag or not at all.
/// </summary>
public sealed record AssetLibraryRow
{
    public required AssetLibraryRowKind Kind { get; init; }

    /// <summary>Unique within one result; a card repeated under two tags gets two keys.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>How many cards a header covers; zero on a card row.</summary>
    public int Count { get; init; }

    public AssetSetSummary? Card { get; init; }

    public bool IsHeader => Kind == AssetLibraryRowKind.Header;

    public static AssetLibraryRow Header(string key, string title, int count) => new()
    {
        Kind = AssetLibraryRowKind.Header,
        Key = "group:" + key,
        Title = title,
        Count = count,
    };

    public static AssetLibraryRow ForCard(string groupKey, AssetSetSummary card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new AssetLibraryRow
        {
            Kind = AssetLibraryRowKind.Card,
            Key = groupKey + "/" + card.SetId,
            Title = card.Name,
            Card = card,
        };
    }
}

/// <summary>
/// The seed of one placement gesture. It is deliberately not <see cref="HashCode"/>: that hash is
/// randomised per process for strings, so the same documented history would replay differently in
/// the next run. This mix (FNV-1a over the tuple's bytes) is fixed for good.
/// </summary>
public static class GestureSeed
{
    private const uint OffsetBasis = 2_166_136_261;
    private const uint Prime = 16_777_619;

    public static int From(long revision, string? setId, int variantIndex, int gestureCounter)
    {
        unchecked
        {
            var hash = MixLong(OffsetBasis, revision);
            hash = MixBytes(hash, Encoding.UTF8.GetBytes(setId ?? ""));

            // A zero word after the identifier keeps "ab" + 1 from mixing to the same value as
            // "a" + "b1" would: the parts stay separable however long the set id is.
            hash = MixInt(hash, 0);
            hash = MixInt(hash, variantIndex);
            hash = MixInt(hash, gestureCounter);
            return (int)hash;
        }
    }

    private static uint MixLong(uint hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return MixBytes(hash, bytes);
    }

    private static uint MixInt(uint hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return MixBytes(hash, bytes);
    }

    private static uint MixBytes(uint hash, ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            foreach (var value in bytes)
            {
                hash = (hash ^ value) * Prime;
            }

            return hash;
        }
    }
}
