using System.IO.Compression;
using System.Text.Json.Serialization;

namespace Map.Core.Documents;

/// <summary>
/// «Красота» of an editable area (R10, schema v27), after Unreal Foliage/PCG, Gaea and Dungeondraft:
/// a soft edge (fewer and smaller objects toward the border), clumps from low-frequency noise,
/// groves of one kind in patches, a cold contact shadow under every object, a dark underlay under
/// the mass (a wide soft blot under each object, all drawn before the objects, so a dense group
/// reads as one mass and a clearing stays light) and a ±5 % colour variation. Every property has its own switch; an area
/// without this record (every area of an older map) is filled and drawn exactly as before.
/// </summary>
public sealed record PlacementAreaBeauty
{
    public const double MaxEdgeWidth = 2000;
    public const double MaxClumpSize = 8000;
    public const double MaxColorVariation = 10;

    /// <summary>«Мягкий край»: within <see cref="EdgeWidth"/> of the border objects thin out and shrink.</summary>
    public bool SoftEdge { get; init; }

    /// <summary>Width of the soft edge, in canvas units.</summary>
    public double EdgeWidth { get; init; } = 120;

    /// <summary>«Кучки и поляны»: density follows a low-frequency noise of the world.</summary>
    public bool Clumps { get; init; }

    /// <summary>How strongly the noise thins and thickens, 0..100 %.</summary>
    public double ClumpStrength { get; init; } = 50;

    /// <summary>Size of one clump or clearing (noise wavelength), in canvas units.</summary>
    public double ClumpSize { get; init; } = 360;

    /// <summary>«Рощи одного вида»: the variant follows a second noise, so one kind grows in patches.</summary>
    public bool SpeciesPatches { get; init; }

    /// <summary>«Тень под объектом»: a soft cold ellipse at the foot, shifted to the south-east.</summary>
    public bool GroundShadow { get; init; }

    /// <summary>Opacity of the contact shadow, 0..1.</summary>
    public double ShadowOpacity { get; init; } = .34;

    /// <summary>«Подложка массива»: a wide soft dark blot under every object, drawn under all of them.</summary>
    public bool Underlay { get; init; }

    /// <summary>Opacity of one blot, 0..1; where several overlap the ground gets darker.</summary>
    public double UnderlayOpacity { get; init; } = .12;

    /// <summary>«Разброс цвета»: brightness and warmth vary by ± this percent, by noise.</summary>
    public bool ColorVariation { get; init; }

    public double ColorVariationPercent { get; init; } = 5;

    [JsonIgnore] public bool Any => SoftEdge || Clumps || SpeciesPatches || GroundShadow || Underlay || ColorVariation;

    /// <summary>
    /// What a newly drawn area gets (R10 ASSUMPTION, see the stage report): the ground (contact
    /// shadow, underlay) and the colour variation always; for groves (every pattern but a ridge)
    /// also a soft edge two object widths wide, gentle clumps about eight widths across and groves
    /// of one kind. A ridge is a narrow chain that already tapers at its ends: a soft edge or clumps
    /// would thin it out, and one kind per patch would make the whole chain one mountain.
    /// Older areas get nothing and keep their look.
    /// </summary>
    public static PlacementAreaBeauty ForNewArea(double baseWidth, PlacementAreaPattern pattern = PlacementAreaPattern.Masses)
    {
        var width = double.IsFinite(baseWidth) && baseWidth > 0 ? baseWidth : 96;
        var groves = pattern != PlacementAreaPattern.Ridge;
        return new()
        {
            SoftEdge = groves, EdgeWidth = Math.Clamp(Math.Round(width * 2), 20, MaxEdgeWidth),
            Clumps = groves, ClumpStrength = 25, ClumpSize = Math.Clamp(Math.Round(width * 8), 80, MaxClumpSize),
            SpeciesPatches = groves, GroundShadow = true, Underlay = true, ColorVariation = true,
        };
    }

    public void Validate()
    {
        if (!double.IsFinite(EdgeWidth) || EdgeWidth < 1 || EdgeWidth > MaxEdgeWidth)
            throw new ArgumentException("Ширина мягкого края — от 1 до 2000.");
        if (!double.IsFinite(ClumpStrength) || ClumpStrength is < 0 or > 100)
            throw new ArgumentException("Сила кучек — от 0 до 100%.");
        if (!double.IsFinite(ClumpSize) || ClumpSize < 10 || ClumpSize > MaxClumpSize)
            throw new ArgumentException("Размер кучки — от 10 до 8000.");
        if (!double.IsFinite(ShadowOpacity) || ShadowOpacity is < 0 or > 1)
            throw new ArgumentException("Тень под объектом — от 0 до 100%.");
        if (!double.IsFinite(UnderlayOpacity) || UnderlayOpacity is < 0 or > 1)
            throw new ArgumentException("Подложка — от 0 до 100%.");
        if (!double.IsFinite(ColorVariationPercent) || ColorVariationPercent is < 0 or > MaxColorVariation)
            throw new ArgumentException("Разброс цвета — от 0 до 10%.");
    }
}

/// <summary>
/// The density brush of an area (R10, schema v27): an 8-bit raster over the area in world space.
/// 128 leaves the density as set; 0 empties the place, 255 doubles it. It multiplies the density
/// a slot is compared with, so - like every other area filter - it removes or returns slots in
/// place and never moves the ones that stay. Stored deflated, so an untouched brush costs bytes.
/// </summary>
public sealed class PlacementAreaDensityMask : IEquatable<PlacementAreaDensityMask>
{
    public const byte Neutral = 128;
    public const int MaxSide = 1024;
    private readonly Lazy<byte[]> _values;

    [JsonConstructor]
    public PlacementAreaDensityMask(double left, double top, double cellSize, int width, int height, string data)
    {
        Left = left; Top = top; CellSize = cellSize; Width = width; Height = height; Data = data ?? "";
        _values = new(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>World position of the first cell's corner.</summary>
    public double Left { get; }
    public double Top { get; }

    /// <summary>World size of one raster cell.</summary>
    public double CellSize { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Base64 of the deflated row-major values.</summary>
    public string Data { get; }

    [JsonIgnore] public double Right => Left + Width * CellSize;
    [JsonIgnore] public double Bottom => Top + Height * CellSize;

    /// <summary>The raster values; a shared array, never to be written. <see cref="CopyValues"/> for edits.</summary>
    internal byte[] Values => _values.Value;

    public byte[] CopyValues() => (byte[])_values.Value.Clone();

    public static PlacementAreaDensityMask Create(double left, double top, double cellSize, int width, int height, byte[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != (long)width * height) throw new ArgumentException("Размер растра плотности не совпадает с данными.");
        using var memory = new MemoryStream();
        using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(values);
        var mask = new PlacementAreaDensityMask(left, top, cellSize, width, height, Convert.ToBase64String(memory.ToArray()));
        mask.Validate();
        return mask;
    }

    /// <summary>Whether every value is neutral: such a brush is not stored at all.</summary>
    [JsonIgnore] public bool IsNeutral => Values.All(v => v == Neutral);

    /// <summary>The multiplier at a world point, bilinear between cells; 1 outside the raster.</summary>
    public double Factor(double x, double y)
    {
        var fx = (x - Left) / CellSize - .5; var fy = (y - Top) / CellSize - .5;
        if (fx < -.5 || fy < -.5 || fx > Width - .5 || fy > Height - .5) return 1;
        var values = Values;
        var x0 = Math.Clamp((int)Math.Floor(fx), 0, Width - 1); var y0 = Math.Clamp((int)Math.Floor(fy), 0, Height - 1);
        var x1 = Math.Min(x0 + 1, Width - 1); var y1 = Math.Min(y0 + 1, Height - 1);
        var tx = Math.Clamp(fx - x0, 0, 1); var ty = Math.Clamp(fy - y0, 0, 1);
        var top = values[y0 * Width + x0] * (1 - tx) + values[y0 * Width + x1] * tx;
        var bottom = values[y1 * Width + x0] * (1 - tx) + values[y1 * Width + x1] * tx;
        return (top * (1 - ty) + bottom * ty) / Neutral;
    }

    public void Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || Math.Abs(Left) > CanvasDimensionPolicy.Maximum * 2d
            || Math.Abs(Top) > CanvasDimensionPolicy.Maximum * 2d || !double.IsFinite(CellSize) || CellSize < 1
            || CellSize > CanvasDimensionPolicy.Maximum || Width < 1 || Height < 1 || Width > MaxSide || Height > MaxSide)
            throw new ArgumentException("Некорректный растр кисти плотности.");
        if (_values.Value.Length != Width * Height) throw new ArgumentException("Данные кисти плотности повреждены.");
    }

    private byte[] Decode()
    {
        try
        {
            var packed = Convert.FromBase64String(Data);
            var values = new byte[Math.Max(0, Width) * Math.Max(0, Height)];
            using var memory = new MemoryStream(packed);
            using var deflate = new DeflateStream(memory, CompressionMode.Decompress);
            var read = 0;
            while (read < values.Length)
            {
                var n = deflate.Read(values, read, values.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read != values.Length || deflate.ReadByte() != -1) return [];
            return values;
        }
        catch (FormatException) { return []; }
        catch (InvalidDataException) { return []; }
    }

    public bool Equals(PlacementAreaDensityMask? other) => other is not null && (ReferenceEquals(this, other)
        || Left.Equals(other.Left) && Top.Equals(other.Top) && CellSize.Equals(other.CellSize)
        && Width == other.Width && Height == other.Height && string.Equals(Data, other.Data, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as PlacementAreaDensityMask);
    public override int GetHashCode() => HashCode.Combine(Left, Top, CellSize, Width, Height, Data.Length);
}
