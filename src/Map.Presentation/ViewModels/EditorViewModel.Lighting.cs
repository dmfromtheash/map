using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The Свет section (DEC-057): one switch for the whole section, the time of day, and separate
/// effects - each with its own switch and strength - plus the map's own lights placed with a click.
/// A slider being dragged only previews (no history entry); its release is one Undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    private MapLighting? _lightingPreview;
    private MapDocument? _lightingPreviewBase;

    /// <summary>The map's light as edited (defaults, switched off, for a map that never used it).</summary>
    public MapLighting Lighting => Session.Document.Lighting ?? new MapLighting { Enabled = false };

    /// <summary>The light shown right now: a slider's preview while it is dragged, else the document's.</summary>
    public MapLighting ShownLighting => ValidLightingPreview ?? Lighting;

    private MapLighting? ValidLightingPreview => ReferenceEquals(_lightingPreviewBase, Session.Document) ? _lightingPreview : null;

    /// <summary>Applies a change to the light: a preview while dragging, or one Undo step on release.</summary>
    public void ChangeLighting(string name, Func<MapLighting, MapLighting> change, bool commit)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(ShownLighting).Normalized();
        if (!commit)
        {
            _lightingPreview = next;
            _lightingPreviewBase = Session.Document;
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _lightingPreview = null;
        _lightingPreviewBase = null;
        if (Session.Document.Lighting == next) { SceneChanged?.Invoke(this, EventArgs.Empty); return; }
        Session.Execute(new SetLightingCommand(name, Session.Document.Lighting, next));
        AfterEdit();
        Raise(nameof(Lighting));
    }

    public void SetLightingEnabled(bool on) => ChangeLighting(on ? "Свет включён" : "Свет выключен", l => l with { Enabled = on }, true);

    /// <summary>0 = day, 0.5 = evening, 1 = night.</summary>
    public void SetTimeOfDay(double t, bool commit) => ChangeLighting("Время суток", l => l with { TimeOfDay = t, Enabled = true }, commit);

    public void SetLightingEffect(LightingEffectKind kind, bool? enabled, double? strength, bool commit) =>
        ChangeLighting(EffectTitle(kind), l =>
        {
            var current = Effect(l, kind);
            var next = new LightingEffect(enabled ?? current.Enabled, strength ?? current.Strength);
            return kind switch
            {
                LightingEffectKind.Relief => l with { Relief = next },
                LightingEffectKind.Shadows => l with { Shadows = next },
                LightingEffectKind.Fog => l with { Fog = next },
                LightingEffectKind.Lights => l with { Lights = next },
                _ => l with { Color = next },
            };
        }, commit);

    public static LightingEffect Effect(MapLighting lighting, LightingEffectKind kind) => kind switch
    {
        LightingEffectKind.Relief => lighting.Relief,
        LightingEffectKind.Shadows => lighting.Shadows,
        LightingEffectKind.Fog => lighting.Fog,
        LightingEffectKind.Lights => lighting.Lights,
        _ => lighting.Color,
    };

    public static string EffectTitle(LightingEffectKind kind) => kind switch
    {
        LightingEffectKind.Relief => "Объём гор",
        LightingEffectKind.Shadows => "Тени",
        LightingEffectKind.Fog => "Туман в низинах",
        LightingEffectKind.Lights => "Огни",
        _ => "Цвет суток",
    };

    // ---------------------------------------------------------------- lights of the map

    /// <summary>True while the next left click on the canvas places a light.</summary>
    public bool LightPlacementArmed { get; private set; }

    /// <summary>Colour and size of the next light placed: torch, window or camp fire.</summary>
    public LightSourcePreset LightPreset { get; set; } = LightSourcePreset.Torch;

    public void ArmLightPlacement(bool armed)
    {
        if (LightPlacementArmed == armed) return;
        LightPlacementArmed = armed;
        StatusDetail = armed ? "Щёлкните по карте, где горит огонь. Закончить — снова «Ставить огонь кликом»." : "";
        Raise(nameof(LightPlacementArmed));
    }

    /// <summary>Places a light at a map point (one Undo step) and keeps placement on for the next one.</summary>
    public void PlaceLightAt(double x, double y)
    {
        var (color, radius, intensity, flicker) = LightPreset switch
        {
            LightSourcePreset.Window => ("#ffc27a", 150.0, 0.8, false),
            LightSourcePreset.Campfire => ("#ff8a3c", 380.0, 1.3, true),
            _ => ("#ff9a42", 280.0, 1.1, true),
        };
        var scale = Math.Max(1, Math.Min(Session.Document.Canvas.Width, Session.Document.Canvas.Height) / 1440.0);
        var source = new MapLightSource(Guid.NewGuid(), x, y, color, radius * scale, intensity, flicker);
        ChangeLighting("Огонь поставлен", l => l with
        {
            Enabled = true,
            Lights = l.Lights.Enabled ? l.Lights : l.Lights with { Enabled = true },
            Sources = [.. l.Sources, source],
        }, true);
    }

    public void RemoveLight(Guid id) =>
        ChangeLighting("Огонь убран", l => l with { Sources = [.. l.Sources.Where(s => s.Id != id)] }, true);

    public void SetLightRadius(Guid id, double radius, bool commit) =>
        ChangeLighting("Размер огня", l => l with { Sources = [.. l.Sources.Select(s => s.Id == id ? s with { Radius = radius } : s)] }, commit);

    public void ClearLights() => ChangeLighting("Все огни убраны", l => l with { Sources = [] }, true);
}

public enum LightingEffectKind { Relief, Shadows, Fog, Lights, Color }

public enum LightSourcePreset { Torch, Window, Campfire }
