namespace Map.Core.Editing;

/// <summary>
/// One concrete image a stamp gesture may place, addressed by content hash. The pixel size is
/// carried so each variant of a set keeps its own aspect ratio at a shared base width.
/// </summary>
public sealed record StampVariantSource(
    string Sha256, int PixelWidth, int PixelHeight, string? MaskSha256 = null,
    double CollisionFraction = StampCollision.DefaultFraction);

/// <summary>Discrete, deterministic random rotation increments for a scatter gesture.</summary>
public enum StampRandomAngleStep
{
    Off = 0,
    Degrees15 = 15,
    Degrees30 = 30,
    Degrees45 = 45,
    Degrees60 = 60,
    Degrees90 = 90,
    Degrees180 = 180,
}

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

    public StampRandomAngleStep RandomAngleStep { get; init; }

    /// <summary>Compatibility adapter for pre-Stage-15 callers; true maps to the smallest 15° step.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RandomAngle
    {
        get => RandomAngleStep != StampRandomAngleStep.Off;
        init => RandomAngleStep = value ? StampRandomAngleStep.Degrees15 : StampRandomAngleStep.Off;
    }

    public bool RandomFlip { get; init; }

    /// <summary>Width of one placed stamp in world units; the height follows the aspect ratio.</summary>
    public double BaseWidth { get; init; } = 96;

    /// <summary>
    /// Continuous per-stamp variation, each drawn from its own seeded sequence so switching one
    /// on never moves the others. Rotation and hue in degrees (± that much), the rest 0..1 as a
    /// fraction (± that fraction of size or opacity, ± that offset of saturation or brightness).
    /// </summary>
    public double RotationJitterDegrees { get; init; }
    public double SizeJitter { get; init; }
    public double OpacityJitter { get; init; }
    public double HueJitterDegrees { get; init; }
    public double SaturationJitter { get; init; }
    public double BrightnessJitter { get; init; }

    /// <summary>
    /// «Без наложений»: a scattered stamp is left out when its collision box would intersect the
    /// box of a stamp already planned in this gesture or listed in <see cref="ExistingCollisions"/>.
    /// Every random draw is still taken, so switching it on only removes stamps, never moves them.
    /// </summary>
    public bool AvoidOverlap { get; init; }

    /// <summary>Boxes of stamps already on the target layer near the gesture.</summary>
    public IReadOnlyList<StampCollisionBox> ExistingCollisions { get; init; } = [];

    /// <summary>Tool rotation applied after planning; the collision boxes are turned by it as well.</summary>
    public double CollisionRotationDegrees { get; init; }

    public bool HasJitter => RotationJitterDegrees > 0 || SizeJitter > 0 || OpacityJitter > 0
        || HueJitterDegrees > 0 || SaturationJitter > 0 || BrightnessJitter > 0;
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
    bool FlipVertical,
    string? MaskSha256 = null,
    double OpacityFactor = 1,
    double HueShift = 0,
    double SaturationShift = 0,
    double BrightnessShift = 0);

/// <summary>
/// Turns a gesture into an immutable placement plan. The renderer never sees this type: it knows
/// only the concrete hash each object already carries, and it never rerolls a random choice.
/// </summary>
public static class StampPlacement
{
    /// <summary>Density is expressed against this many square world units.</summary>
    public const double DensityReferenceArea = 10_000;

    public const int MinStampsPerGesture = 1;

    /// <summary>
    /// count = round(density * PI * radius^2 / 10000). There is no product-level stamp ceiling;
    /// <see cref="int.MaxValue"/> is only the CLR collection/addressing boundary.
    /// </summary>
    public static int CountFor(double density, double radius)
    {
        if (double.IsNaN(density) || double.IsNaN(radius) || density <= 0 || radius <= 0)
        {
            return MinStampsPerGesture;
        }

        var area = Math.PI * radius * radius;
        var raw = Math.Round(density * area / DensityReferenceArea, MidpointRounding.AwayFromZero);
        if (raw >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(MinStampsPerGesture, (int)raw);
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
        => PlanArea(request, variants, int.MaxValue);

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
        if (maximumItems < 1)
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

        var attempts = CountFor(request.Density, request.AreaRadius);
        // Without the no-overlap rule the plan is the familiar prefix; with it every attempt is
        // drawn and the preview keeps the first accepted stamps of the same sequence.
        var count = request.AvoidOverlap ? attempts : Math.Min(attempts, maximumItems);
        var collisions = request.AvoidOverlap ? CollisionGrid(request, variants) : null;
        var random = new DeterministicRandom(request.Seed);
        // The jitters have a sequence of their own: a gesture recorded before they existed keeps
        // every position, variant, angle step and flip it had.
        var jitter = new DeterministicRandom(unchecked(request.Seed * 31 + 0x5f3759df));
        var items = new List<StampPlacementItem>(Math.Min(count, Math.Min(maximumItems, 4096)));
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
            var step = (int)request.RandomAngleStep;
            double rotation = step > 0 && 360 % step == 0
                ? Math.Min((int)(rotationDraw * (360 / step)), (360 / step) - 1) * step
                : 0;
            var item = Item(
                request,
                variant,
                x,
                y,
                rotation,
                request.RandomFlip && flipHorizontalDraw < 0.5,
                request.RandomFlip && flipVerticalDraw < 0.25);
            var placed = Jittered(request, item, jitter);
            if (collisions is not null)
            {
                var box = StampCollision.For(placed, variant.CollisionFraction, request.CollisionRotationDegrees);
                if (collisions.Collides(box)) continue;
                collisions.Add(box);
            }
            items.Add(placed);
            if (items.Count >= maximumItems) break;
        }

        return items;
    }

    private static StampCollisionGrid CollisionGrid(StampPlacementRequest request, IReadOnlyList<StampVariantSource> variants)
    {
        var width = request.BaseWidth <= 0 ? 1 : request.BaseWidth;
        var tallest = variants.Max(v => v.PixelWidth <= 0 ? 1.0 : (double)v.PixelHeight / v.PixelWidth);
        var grid = new StampCollisionGrid(Math.Max(width, width * tallest) * (1 + Math.Clamp(request.SizeJitter, 0, 1)));
        foreach (var existing in request.ExistingCollisions) grid.Add(existing);
        return grid;
    }

    /// <summary>
    /// The continuous variations of one stamp, every draw taken whether used or not. The size
    /// change keeps the stamp centred where the plan put it.
    /// </summary>
    private static StampPlacementItem Jittered(StampPlacementRequest request, StampPlacementItem item, DeterministicRandom jitter)
    {
        var rotationDraw = (jitter.NextDouble() * 2) - 1;
        var sizeDraw = (jitter.NextDouble() * 2) - 1;
        var opacityDraw = jitter.NextDouble();
        var hueDraw = (jitter.NextDouble() * 2) - 1;
        var saturationDraw = (jitter.NextDouble() * 2) - 1;
        var brightnessDraw = (jitter.NextDouble() * 2) - 1;
        if (!request.HasJitter)
        {
            return item;
        }

        var scale = 1 + (Math.Clamp(request.SizeJitter, 0, 1) * sizeDraw);
        scale = Math.Max(0.05, scale);
        var width = item.Width * scale;
        var height = item.Height * scale;
        var rotation = item.RotationDegrees + (Math.Clamp(request.RotationJitterDegrees, 0, 180) * rotationDraw);
        rotation = ((rotation % 360) + 360) % 360;
        return item with
        {
            X = item.X + ((item.Width - width) / 2),
            Y = item.Y + ((item.Height - height) / 2),
            Width = width,
            Height = height,
            RotationDegrees = rotation,
            OpacityFactor = 1 - (Math.Clamp(request.OpacityJitter, 0, 1) * opacityDraw),
            HueShift = Math.Clamp(request.HueJitterDegrees, 0, 180) * hueDraw,
            SaturationShift = Math.Clamp(request.SaturationJitter, 0, 1) * saturationDraw,
            BrightnessShift = Math.Clamp(request.BrightnessJitter, 0, 1) * brightnessDraw,
        };
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
            flipVertical,
            variant.MaskSha256);
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
