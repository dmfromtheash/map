namespace Map.Core.Resources;

/// <summary>One named consumer of the disk budget for prepared data and the part of it guaranteed to that consumer.</summary>
public sealed record PreparedDataShare(string Name, decimal GuaranteedFraction);

/// <summary>
/// The one disk budget of this PC for data Map prepares by itself and can always rebuild (DEC-106, DEC-110, DEC-111):
/// asset preparations now, the drawn-map tile pyramid later. The total is at most <see cref="DefaultTotalBytes"/> and
/// never so much that the drive keeps less than <see cref="DefaultFreeSpaceFraction"/> of its free space or
/// <see cref="DefaultMinimumFreeBytes"/>, whichever is larger. Each share is guaranteed its fraction of the total and
/// may borrow what the other shares do not use right now; a consumer that needs its guarantee back gets it at the next
/// cleanup of the borrower. Consumers report their measured bytes after each cleanup. No consumer deletes another's files.
/// </summary>
public sealed class PreparedDataBudget
{
    public const long GiB = 1024L * 1024 * 1024;
    /// <summary>DEC-111: «до 5 ГБ» on the owner's PC (Windows shows GiB as «ГБ»).</summary>
    public const long DefaultTotalBytes = 5 * GiB;
    /// <summary>Prepared data may take at most this share of the drive's free space.</summary>
    public const decimal DefaultFreeSpaceFraction = 0.10m;
    /// <summary>The drive always keeps at least this much free for Windows, saves and exports.</summary>
    public const long DefaultMinimumFreeBytes = 10 * GiB;

    /// <summary>Machine cache of asset preparations (cache/derived): 60 % guaranteed = 3 GiB of 5.</summary>
    public static readonly PreparedDataShare DerivedAssets = new("derived-assets", 0.60m);
    /// <summary>Reserved for the prepared pyramid of the drawn map (DEC-111 p.3): 40 % guaranteed = 2 GiB of 5.</summary>
    public static readonly PreparedDataShare DocumentPyramid = new("document-pyramid", 0.40m);

    public static PreparedDataBudget Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _usage = new(StringComparer.Ordinal);

    public PreparedDataBudget(long totalBytes = DefaultTotalBytes, decimal freeSpaceFraction = DefaultFreeSpaceFraction,
        long minimumFreeBytes = DefaultMinimumFreeBytes, IReadOnlyList<PreparedDataShare>? shares = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        if (freeSpaceFraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(freeSpaceFraction));
        Shares = shares ?? [DerivedAssets, DocumentPyramid];
        if (Shares.Count == 0 || Shares.Any(share => share.GuaranteedFraction is < 0 or > 1)
            || Shares.Sum(share => share.GuaranteedFraction) > 1
            || Shares.Select(share => share.Name).Distinct(StringComparer.Ordinal).Count() != Shares.Count)
            throw new ArgumentException("Shares must have distinct names and guarantees summing to at most 1.", nameof(shares));
        TotalBytes = totalBytes; FreeSpaceFraction = freeSpaceFraction; MinimumFreeBytes = minimumFreeBytes;
    }

    public long TotalBytes { get; }
    public decimal FreeSpaceFraction { get; }
    public long MinimumFreeBytes { get; }
    public IReadOnlyList<PreparedDataShare> Shares { get; }

    /// <summary>
    /// The whole budget right now. Free space already excludes prepared data, so the reserve permits at most
    /// preparedBytes + free - reserve to remain. Zero means nothing may be kept, even if cleanup cannot restore the reserve.
    /// </summary>
    public static long CalculateTotal(long maximumBytes, decimal freeSpaceFraction, long minimumFreeBytes,
        long availableFreeBytes, long preparedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(preparedBytes);
        return (long)Math.Min(maximumBytes, Math.Min(
            decimal.Floor(availableFreeBytes * freeSpaceFraction),
            Math.Max(0m, (decimal)preparedBytes + availableFreeBytes - minimumFreeBytes)));
    }

    /// <summary>Last measured bytes of one share (0 until its consumer reported).</summary>
    public long UsageOf(PreparedDataShare share)
    {
        lock (_gate) return _usage.GetValueOrDefault(Require(share).Name);
    }

    public void ReportUsage(PreparedDataShare share, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate) _usage[Require(share).Name] = bytes;
    }

    /// <summary>
    /// Ceiling of one share: its guarantee of the current total, or more while the other shares leave room
    /// (total minus what they last reported), never more than the total.
    /// </summary>
    public long CeilingFor(PreparedDataShare share, long availableFreeBytes, long shareBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shareBytes);
        long others;
        lock (_gate)
            others = _usage.Where(pair => !string.Equals(pair.Key, Require(share).Name, StringComparison.Ordinal)).Sum(pair => pair.Value);
        var total = CalculateTotal(TotalBytes, FreeSpaceFraction, MinimumFreeBytes, availableFreeBytes,
            checked(shareBytes + others));
        var guaranteed = (long)decimal.Floor(total * share.GuaranteedFraction);
        return Math.Clamp(Math.Max(guaranteed, total - others), 0, total);
    }

    private PreparedDataShare Require(PreparedDataShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return Shares.FirstOrDefault(known => string.Equals(known.Name, share.Name, StringComparison.Ordinal))
            ?? throw new ArgumentException("Unknown prepared-data share: " + share.Name, nameof(share));
    }
}
