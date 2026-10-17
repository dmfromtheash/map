using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The asset panel counts what differs from the base placement settings on "К базовым" and marks
/// the folded sections that hide a change. The list must be empty for the base values and name
/// each change in the section it belongs to.
/// </summary>
public sealed class StampSettingChangesTests
{
    [Fact]
    public void Base_settings_have_no_changes()
    {
        Assert.Empty(EditorViewModel.DescribeStampChanges(new StampToolSettings()));
        var boundToVariant = new StampToolSettings { Style = new StampStyle(new string('a', 64)) };
        Assert.Empty(EditorViewModel.DescribeStampChanges(boundToVariant));
    }

    [Fact]
    public void Each_change_lands_in_its_own_section()
    {
        var settings = new StampToolSettings
        {
            Mode = StampPlacementMode.Area,
            Density = 80,
            ScalePercent = 150,
            RotationDegrees = 15,
            Style = new StampStyle(new string('b', 64)) { ShadowOpacity = 0.4, FlipHorizontal = true },
            RandomAngleStep = StampRandomAngleStep.Degrees30,
        };

        var changes = EditorViewModel.DescribeStampChanges(settings);

        Assert.Equal(["режим", "плотность"], changes.Where(c => c.Group == StampSettingGroup.Placement).Select(c => c.Label));
        Assert.Equal(["масштаб 150%", "поворот 15°", "отражение"],
            changes.Where(c => c.Group == StampSettingGroup.NextAsset).Select(c => c.Label));
        Assert.Equal(["тень"], changes.Where(c => c.Group == StampSettingGroup.Appearance).Select(c => c.Label));
        Assert.Equal(["случайный угол"], changes.Where(c => c.Group == StampSettingGroup.Randomness).Select(c => c.Label));
    }

    [Fact]
    public void Mode_numbers_count_only_for_the_mode_that_uses_them()
    {
        // A changed scatter radius does nothing while placing one object at a time.
        var settings = new StampToolSettings { AreaRadius = 400, SeriesSpacingPercent = 120 };
        Assert.Empty(EditorViewModel.DescribeStampChanges(settings));
    }
}
