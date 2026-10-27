using Map.Core.Documents;

namespace Map.Core.Tests;

public sealed class MapStyleTests
{
    [Theory]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.UkiyoeSosakuHanga)]
    [InlineData(MapStylePreset.PrintLetterpress)]
    [InlineData(MapStylePreset.PrintTintedLithograph)]
    [InlineData(MapStylePreset.OilAllaPrima)]
    [InlineData(MapStylePreset.OilImpastoRhythm)]
    [InlineData(MapStylePreset.CharcoalVineStump)]
    [InlineData(MapStylePreset.CharcoalHatching)]
    [InlineData(MapStylePreset.BlueprintSurvey)]
    [InlineData(MapStylePreset.MoonlightHasui)]
    [InlineData(MapStylePreset.MoonlightPastel)]
    public void Approved_presets_expose_only_their_own_effects_and_normalize_each_value(MapStylePreset preset)
    {
        var original = MapStyle.For(preset);
        Assert.Equal(preset, original.Normalized().Preset);
        Assert.Equal(preset == MapStylePreset.WatercolorBigShapes, MapStyle.EffectsOf(preset).Contains(StyleEffectKind.Bleed));
        Assert.DoesNotContain(StyleEffectKind.Mist, MapStyle.EffectsOf(preset));
        foreach (var kind in MapStyle.EffectsOf(preset))
        {
            Assert.True(original.Effect(kind).Enabled);
            var high = original.WithEffect(kind, new StyleEffect(true, 5)).Normalized();
            var low = original.WithEffect(kind, new StyleEffect(false, -5)).Normalized();
            Assert.Equal(1, high.Effect(kind).Strength);
            Assert.Equal(0, low.Effect(kind).Strength);
            Assert.Equal(0, low.Effect(kind).Effective);
            Assert.Equal(1, original.Effect(kind).Strength);
        }
        Assert.Equal(preset == MapStylePreset.OldMapBlaeu,
            MapStyle.EffectsOf(preset).Contains(StyleEffectKind.ColourWash));
        Assert.Equal(preset == MapStylePreset.EngravingAquatint,
            MapStyle.EffectsOf(preset).Contains(StyleEffectKind.Aquatint));
    }
}
