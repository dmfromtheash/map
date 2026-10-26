using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private bool _landPaintMode;
    private BrushSettings? _landShapeBrush;
    private BrushSettings _landMaterialBrush = new() { Radius = 64, Softness = .8, Spacing = .15, Color = "#FF9BA879" };

    public bool LandPaintMode => _landPaintMode;
    public string LandMaterialId { get; private set; } = "";
    public bool LandPaintWithColor { get; private set; } = true;
    public bool LandPaintEraser { get; private set; }
    public double LandMaterialScale { get; set; } = 512;
    public double LandMaterialRotation { get; set; }
    public IReadOnlyList<string> RecentLandMaterials => _recentLandMaterials;
    private readonly List<string> _recentLandMaterials = [];

    public void SetLandPaintMode(bool paint)
    {
        if (!paint && _landCliffMaskMode) { _landCliffMaskMode = false; Raise(nameof(LandCliffMaskMode)); Raise(nameof(CliffMaskLayerId)); }
        if (_landPaintMode == paint) return;
        CancelStroke(); CancelLandShapeGesture();
        CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        if (paint)
        {
            _landShapeBrush = BrushSettings;
            BrushSettings = _landMaterialBrush;
        }
        else
        {
            _landMaterialBrush = BrushSettings;
            BrushSettings = _landShapeBrush ?? new BrushSettings();
        }
        _landPaintMode = paint;
        if (paint && LandMaterialId.Length == 0) SetLandPaintAction("Texture");
        Raise(nameof(LandPaintMode)); Raise(nameof(LandDrawsRegion));
        StatusDetail = paint ? "Кисть раскрашивает землю внутри её берегов." : "Изменение формы земли.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLandPaintAction(string action)
    {
        if (action == "Texture" && MapTextureLibrary.Image(LandMaterialId) is null)
        {
            var first = MapFillTextures.Of(MapFillTextureKind.Ground)
                .FirstOrDefault(material => MapTextureLibrary.Image(material.Id) is not null);
            if (first is not null) ChooseLandMaterial(first.Id);
            else StatusDetail = "Материалы пока не добавлены. Можно рисовать цветом.";
            return;
        }
        CancelStroke();
        CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        LandPaintWithColor = action == "Color";
        LandPaintEraser = action == "Erase";
        Raise(nameof(LandPaintWithColor)); Raise(nameof(LandPaintEraser));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool ChooseLandMaterial(string id)
    {
        if (MapFillTextures.Find(id) is not { Kind: MapFillTextureKind.Ground } material
            || MapTextureLibrary.Image(id) is null) return false;
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        LandMaterialId = id;
        LandPaintWithColor = false; LandPaintEraser = false;
        _recentLandMaterials.Remove(id); _recentLandMaterials.Insert(0, id);
        if (_recentLandMaterials.Count > 6) _recentLandMaterials.RemoveAt(6);
        Raise(nameof(LandMaterialId)); Raise(nameof(RecentLandMaterials));
        StatusDetail = $"Материал кисти: {material.Title}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ApplyLandBrushPreset(string preset)
    {
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        BrushSettings = BrushSettings with
        {
            Tip = null, Spacing = .15, Scatter = 0, SizeJitter = 0, OpacityJitter = 0,
            RandomFlipHorizontal = false, RandomFlipVertical = false,
            Softness = preset == "Crisp" ? .08 : .85,
        };
        if (preset == "Dry")
            BrushSettings = BrushSettings with { Softness = .45, Spacing = .3, Scatter = .22, SizeJitter = .3, OpacityJitter = .3 };
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private BrushSettings LandPaintSettings()
    {
        var texture = LandPaintWithColor || LandPaintEraser ? null : MapTextureLibrary.BrushTexture(LandMaterialId);
        return BrushSettings with
        {
            Texture = texture, Eraser = LandPaintEraser,
            Color = LandPaintWithColor ? BrushSettings.Color : "#FFFFFFFF",
            TextureScale = texture is null ? 1 : Math.Clamp(LandMaterialScale, 32, 4096) / texture.Width,
            TextureRotationDegrees = LandMaterialRotation, TextureOffsetX = 0, TextureOffsetY = 0,
            TextureOpacity = 1, HueDegrees = 0, Saturation = 0, Brightness = 0, Contrast = 0,
        };
    }

    public bool ClearLandPaint()
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || land.PaintTiles.TileCount == 0) return false;
        var before = land.PaintTiles.Keys.ToDictionary(k => k, k => land.PaintTiles.TryGet(k));
        var after = before.Keys.ToDictionary(k => k, _ => (byte[]?)null);
        Session.Execute(new LandPaintCommand(land.Id, before, after, "Clear land material painting"));
        AfterEdit(); return true;
    }
}
