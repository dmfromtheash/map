using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class StyleEditorTests
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
    public void Preset_and_slider_release_are_each_one_undo_step(MapStylePreset preset)
    {
        var editor = new EditorViewModel(TestData.Document(), null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.ApplyStylePreset(preset);
        Assert.Equal(MapStyle.For(preset), editor.Session.Document.Style);
        editor.Undo(); Assert.Null(editor.Session.Document.Style);
        editor.Redo(); Assert.Equal(preset, editor.Session.Document.Style!.Preset);
        foreach (var kind in MapStyle.EffectsOf(preset))
        {
            var before = editor.Session.Document.Style;
            editor.SetStyleEffect(kind, null, .2, false);
            editor.SetStyleEffect(kind, null, .4, false);
            Assert.Equal(before, editor.Session.Document.Style);
            Assert.Equal(.4, editor.ShownStyle.Effect(kind).Strength);
            editor.SetStyleEffect(kind, null, .4, true);
            Assert.Equal(.4, editor.Session.Document.Style!.Effect(kind).Strength);
            editor.Undo(); Assert.Equal(before, editor.Session.Document.Style);
            editor.SetStyleEffect(kind, false, null, true);
            Assert.False(editor.Session.Document.Style!.Effect(kind).Enabled);
            editor.Undo(); Assert.Equal(before, editor.Session.Document.Style);
        }
        var original = editor.Session.Document.Style;
        editor.SetStyleStrength(.3, false); editor.SetStyleStrength(.6, false);
        Assert.Equal(original, editor.Session.Document.Style);
        editor.SetStyleStrength(.6, true);
        editor.Undo(); Assert.Equal(original, editor.Session.Document.Style);
        Assert.NotEqual("Тушь", EditorViewModel.StyleFamilyTitle(preset));
    }
}
