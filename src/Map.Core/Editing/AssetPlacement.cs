namespace Map.Core.Editing;

/// <summary>
/// One concrete image a stamp gesture may place, addressed by content hash. The pixel size is
/// carried so each variant of a set keeps its own aspect ratio at a shared base width.
/// </summary>
public sealed record StampVariantSource(string Sha256, int PixelWidth, int PixelHeight);

/// <summary>
/// Everything one stamp gesture needs, resolved before any command exists. Area is a radius in
/// world units and Density is stamps per 10 000 square world units; both are the product's own
/// documented semantics, not a demo count.
/// </summary>
public sealed record StampPlacementRequest
{
    public double CenterX { get; init; }

    public double CenterY { get; init; }

    /// <summary>Radius of the gesture in world units.</summary>
    public double AreaRadius { get; init; }

    /// <summary>Stamps per <see cref="StampPlacement.DensityReferenceArea"/> square world units.</summary>
    public double Density { get; init; }

    public int Seed { get; init; }

    public bool RandomizeVariant { get; init; }

    public bool RandomAngle { get; init; }

    public bool RandomFlip { get; init; }

    /// <summary>Width of one placed stamp in world units; the height follows the aspect ratio.</summary>
    public double BaseWidth { get; init; } = 96;
}

/// <summary>
/// One fully resolved stamp: position, size, transform and the exact content hash. Nothing here
/// is recomputed later, so undo and redo replay the identical gesture.
/// </summary>
public sealed record StampPlacementItem(
    string Sha256,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    bool FlipHorizontal,
    bool FlipVertical);

/// <summary>
/// Turns a gesture into an immutable placement plan. The renderer never sees this type: it knows
/// only the concrete hash each object already carries, and it never rerolls a random choice.
/// </summary>
public static class StampPlacement
{
    /// <summary>Density is expressed against this many square world units.</summary>
    public const double DensityReferenceArea = 10_000;

    /// <summary>A single gesture is bounded so one drag can never allocate without limit.</summary>
    public const int MaxStampsPerGesture = 5_000;

    public const int MinStampsPerGesture = 1;

    /// <summary>count = clamp(round(density * PI * radius^2 / 10000), 1, 5000).</summary>
    public static int CountFor(double density, double radius)
    {
        if (double.IsNaN(density) || double.IsNaN(radius) || density <= 0 || radius <= 0)
        {
            return MinStampsPerGesture;
        }

        var area = Math.PI * radius * radius;
        var raw = Math.Round(density * area / DensityReferenceArea, MidpointRounding.AwayFromZero);
        if (raw >= MaxStampsPerGesture)
        {
            return MaxStampsPerGesture;
        }

        return (int)Math.Clamp(raw, MinStampsPerGesture, MaxStampsPerGesture);
    }

    /// <summary>The same radius expressed in grid cells, or null when no grid is active.</summary>
    public static double? RadiusInCells(double radius, double cellSize) =>
        cellSize > 0 ? radius / cellSize : null;

    /// <summary>The one stamp a click places: the current variant, centred on the pointer.</summary>
    public static IReadOnlyList<StampPlacementItem> PlanOne(
        StampPlacementRequest request, StampVariantSource variant)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(variant);
        return [Item(request, variant, request.CenterX, request.CenterY, 0, false, false)];
    }

    /// <summary>
    /// The full area gesture. Points are sampled uniformly inside the circle and the variant,
    /// angle and flips are all drawn from the same seeded sequence, so replaying the request
    /// reproduces the plan exactly.
    /// </summary>
    public static IReadOnlyList<StampPlacementItem> PlanArea(
        StampPlacementRequest request, IReadOnlyList<StampVariantSource> variants)
        => PlanArea(request, variants, MaxStampsPerGesture);

    /// <summary>
    /// A deterministic prefix of the full scatter plan for transient editor rendering. The same
    /// random draws are consumed in the same order, so every visible sample is byte-for-byte the
    /// item that a later successful commit will materialise. The bounded prefix prevents a dense
    /// gesture from creating thousands of preview objects on every pointer move.
    /// </summary>
    public static IReadOnlyList<StampPlacementItem> PlanAreaPreview(
        StampPlacementRequest request,
        IReadOnlyList<StampVariantSource> variants,
        int maximumItems = 64)
    {
        if (maximumItems is < 1 or > MaxStampsPerGesture)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        return PlanArea(request, variants, maximumItems);
    }

    private static IReadOnlyList<StampPlacementItem> PlanArea(
        StampPlacementRequest request,
        IReadOnlyList<StampVariantSource> variants,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Count == 0)
        {
            throw new ArgumentException("Размещение требует хотя бы один вариант.", nameof(variants));
        }

        var count = Math.Min(CountFor(request.Density, request.AreaRadius), maximumItems);
        var random = new DeterministicRandom(request.Seed);
        var items = new List<StampPlacementItem>(count);
        for (var index = 0; index < count; index++)
        {
            // Uniform inside a disc: sqrt on the radius, otherwise the centre would crowd.
            var radius = request.AreaRadius * Math.Sqrt(random.NextDouble());
            var angle = random.NextDouble() * Math.Tau;
            var x = request.CenterX + (radius * Math.Cos(angle));
            var y = request.CenterY + (radius * Math.Sin(angle));

            // Every draw happens for every stamp whether or not it is used, so toggling one
            // randomisation never shifts the sequence the other ones see.
            var variantDraw = random.NextDouble();
            var rotationDraw = random.NextDouble();
            var flipHorizontalDraw = random.NextDouble();
            var flipVerticalDraw = random.NextDouble();

            var variant = request.RandomizeVariant
                ? variants[Math.Min(variants.Count - 1, (int)(variantDraw * variants.Count))]
                : variants[0];
            var rotation = request.RandomAngle
                ? Math.Round((rotationDraw * 360) - 180, 3)
                : 0;
            items.Add(Item(
                request,
                variant,
                x,
                y,
                rotation,
                request.RandomFlip && flipHorizontalDraw < 0.5,
                request.RandomFlip && flipVerticalDraw < 0.25));
        }

        return items;
    }

    /// <summary>
    /// The variant a set moves to after a successful placement when the user asked for a random
    /// one. Seeded, so the same gesture history always produces the same sequence. The draw is
    /// taken from the OTHER variants: landing on the current one again would read as a dead
    /// toggle rather than as randomisation.
    /// </summary>
    public static int NextVariantIndex(int seed, int currentIndex, int variantCount)
    {
        if (variantCount <= 1)
        {
            return 0;
        }

        var current = Math.Clamp(currentIndex, 0, variantCount - 1);
        var random = new DeterministicRandom(seed);
        var draw = Math.Clamp((int)(random.NextDouble() * (variantCount - 1)), 0, variantCount - 2);
        return draw >= current ? draw + 1 : draw;
    }

    private static StampPlacementItem Item(
        StampPlacementRequest request,
        StampVariantSource variant,
        double centerX,
        double centerY,
        double rotation,
        bool flipHorizontal,
        bool flipVertical)
    {
        var width = request.BaseWidth <= 0 ? 1 : request.BaseWidth;
        var aspect = variant.PixelWidth <= 0 ? 1.0 : (double)variant.PixelHeight / variant.PixelWidth;
        var height = width * aspect;
        return new StampPlacementItem(
            variant.Sha256,
            centerX - (width / 2),
            centerY - (height / 2),
            width,
            height,
            rotation,
            flipHorizontal,
            flipVertical);
    }
}

/// <summary>
/// A small explicit PRNG (SplitMix64). The framework generator is not contractually stable across
/// runtimes, and a placement plan must replay identically for undo, redo and golden fixtures.
/// </summary>
public struct DeterministicRandom(int seed)
{
    private ulong _state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL) + 0x2545F4914F6CDD1DUL;

    public double NextDouble()
    {
        _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
        var z = _state;
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;

        // 53 significant bits, the exact precision a double can carry.
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
}
