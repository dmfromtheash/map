using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// G1 (2026-11-12): layout 5 places mountains in size classes by their place, as reference maps do — big peaks in the
/// middle of a range, smaller toward its ends; a massif of dominant peaks with smaller ones around; the same seed
/// gives the same arrangement; «Горный массив» exists only in layout 5.
/// </summary>
public sealed class PlacementAreaRangeLayoutTests
{
    private static readonly string Sha = new('c', 64);

    private static PlacementArea Area(PlacementAreaPattern pattern) => PlacementAreaPlanner.InitializeComposition(new PlacementArea
    {
        Name = "Горы", LayoutVersion = 5, Density = 100, Seed = 7, Pattern = pattern, JoinEdges = true, SizeVariationPercent = 20,
        Nodes = [new(Guid.NewGuid(), 0, 0), new(Guid.NewGuid(), 3000, 0), new(Guid.NewGuid(), 3000, 1200), new(Guid.NewGuid(), 0, 1200)],
        Variants = [new(Sha, 100, 100)], Style = new(Sha), Request = new() { BaseWidth = 160 },
    });

    private static List<PlacementAreaComposition.Sample> Samples(PlacementArea area)
    {
        var layout = new PlacementAreaLayout(area);
        var samples = new List<PlacementAreaComposition.Sample>();
        foreach (var span in PlacementAreaGeometry.Coverage(area.Nodes))
            for (var x = span.MinX; x <= span.MaxX; x++) samples.AddRange(layout.Cell(x, span.Y));
        return samples;
    }

    [Fact]
    public void A_range_has_three_size_classes_and_its_big_peaks_stand_in_the_middle()
    {
        var area = Area(PlacementAreaPattern.Ridge);
        var samples = Samples(area);
        // Classes 1 : 0.62 : 0.34 with ±10 % jitter: big, mid and hill scales do not blend into one another.
        Assert.Contains(samples, s => s.Scale > .85);
        Assert.Contains(samples, s => s.Scale is > .5 and < .75);
        Assert.Contains(samples, s => s.Scale < .42);
        var origin = area.CompositionOrigin; var angle = area.CompositionAngleDegrees * Math.PI / 180;
        double Along(PlacementAreaComposition.Sample s) =>
            Math.Abs((s.Point!.Value.X - origin.X) * Math.Cos(angle) + (s.Point!.Value.Y - origin.Y) * Math.Sin(angle));
        var big = samples.Where(s => s.Scale > .85).Select(Along).ToArray();
        var hills = samples.Where(s => s.Scale < .42).Select(Along).ToArray();
        Assert.True(big.Average() < hills.Average(), "big peaks keep to the middle of the range, hills spread to its ends");
        // The spine comes first as density grows: big peaks activate before hills.
        Assert.True(samples.Where(s => s.Scale > .85).Average(s => s.Activation) < samples.Where(s => s.Scale < .42).Average(s => s.Activation));
    }

    [Fact]
    public void A_massif_has_dominant_peaks_with_smaller_ones_around_and_the_same_seed_repeats_it()
    {
        var area = Area(PlacementAreaPattern.Peaks);
        var first = Samples(area); var again = Samples(area);
        Assert.Equal(first.Select(s => (s.Point, s.Scale)), again.Select(s => (s.Point, s.Scale)));
        var big = first.Count(s => s.Scale > .85); var smaller = first.Count(s => s.Scale <= .85);
        Assert.InRange(big, 1, first.Count / 4);
        Assert.True(smaller > big * 3, "every dominant peak has several smaller ones");
    }

    [Fact]
    public void Peaks_exist_only_in_layout_five_and_need_the_newer_schema()
    {
        var area = Area(PlacementAreaPattern.Peaks);
        area.Validate();
        Assert.True(area.NeedsRangeSchema);
        Assert.Throws<ArgumentException>(() => (area with { LayoutVersion = 4 }).Validate());
        Assert.False((Area(PlacementAreaPattern.Masses) with { LayoutVersion = 4 }).NeedsRangeSchema);
    }
}
