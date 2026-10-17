using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public sealed class FilterColorEffectsTests
{
    [Fact]
    public void Twelve_presets_with_russian_titles()
    {
        Assert.Equal(
            ["Ясность", "Насыщенность", "Выцветшая", "Резкость", "Сепия", "Серая", "Чёрно-белая",
             "Зима", "Красное небо", "Ночь", "Пасмурно", "Пергамент"],
            FilterColorEffects.Presets.Select(preset => preset.Title));
        Assert.Equal(12, FilterColorEffects.Presets.Select(preset => preset.Id).Distinct().Count());
        Assert.All(FilterColorEffects.Presets, preset =>
            Assert.True(FilterColorEffects.IsActive(FilterColorEffects.WithPreset(new FilterSettings(), preset))));
    }

    [Fact]
    public void Default_settings_and_zero_strength_change_nothing()
    {
        Assert.False(FilterColorEffects.IsActive(new FilterSettings()));
        var sepia = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("sepia")!);
        Assert.Equal(((byte)80, (byte)96, (byte)112), FilterColorEffects.Apply(sepia with { ColorStrength = 0 }, 80, 96, 112));
        Assert.Equal(((byte)80, (byte)96, (byte)112), FilterColorEffects.Apply(new FilterSettings(), 80, 96, 112));
    }

    [Fact]
    public void Grey_removes_colour_and_sepia_warms_it()
    {
        var grey = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("grey")!);
        var (r, g, b) = FilterColorEffects.Apply(grey, 40, 160, 220);
        Assert.InRange(Math.Abs(r - g) + Math.Abs(g - b), 0, 2);

        var sepia = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("sepia")!);
        var warm = FilterColorEffects.Apply(sepia, 80, 96, 112);
        Assert.True(warm.R > warm.G && warm.G > warm.B, $"sepia gave {warm}");
    }

    [Fact]
    public void Half_strength_is_half_of_each_change()
    {
        var vivid = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("vivid")!);
        var full = FilterColorEffects.Apply(vivid, 200, 120, 60);
        var half = FilterColorEffects.Apply(vivid with { ColorStrength = 0.5 }, 200, 120, 60);
        Assert.InRange(half.R, (byte)200, full.R);
        Assert.InRange(half.B, full.B, (byte)60);
        Assert.NotEqual(full, half);
    }

    [Fact]
    public void Preset_keeps_the_old_tone_and_texture_and_clear_removes_only_colour()
    {
        var settings = new FilterSettings { Temperature = 0.3, TextureAssetSha256 = new string('a', 64), TextureOpacity = 0.4 };
        var night = FilterColorEffects.WithPreset(settings with { ColorStrength = 0.2 }, FilterColorEffects.Preset("night")!);
        Assert.Equal(("night", 1.0, 0.3, 0.4), (night.ColorPresetId, night.ColorStrength, night.Temperature, night.TextureOpacity));
        var cleared = FilterColorEffects.WithoutColor(night);
        Assert.False(FilterColorEffects.IsActive(cleared));
        Assert.Equal(settings, cleared);
    }
}
