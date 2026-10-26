using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>What one stroke of the «маска обрыва» brush says about the edge under it.</summary>
public enum CliffMaskAction
{
    /// <summary>Здесь стена: the wall stands even where the style leaves a gap.</summary>
    Wall,
    /// <summary>Здесь пляж: the land meets the ground or sea with no wall.</summary>
    Beach,
    /// <summary>Стереть: the edge follows the style again.</summary>
    Erase,
}

/// <summary>
/// R12: the «маска обрыва» brush of the Land tool. It paints over the coast of the active land
/// where its cliff must stand or must give way to a beach; the wall follows on release.
/// </summary>
public sealed partial class EditorViewModel
{
    private bool _landCliffMaskMode;
    private RasterStrokeSession? _cliffCompanion;

    public bool LandCliffMaskMode => _landCliffMaskMode;
    public CliffMaskAction LandCliffMaskAction { get; private set; } = CliffMaskAction.Beach;

    /// <summary>The land whose mask is tinted on the canvas while the brush is armed.</summary>
    public LayerId? CliffMaskLayerId =>
        ActiveTool == EditorTool.Land && _landCliffMaskMode && ActiveLayer is LandLayer land ? land.Id : null;

    public void SetLandCliffMaskMode(bool on)
    {
        if (on == _landCliffMaskMode && (!on || LandPaintMode)) return;
        if (on) { SetLandReliefMode(false); SetLandPaintMode(true); }
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        _landCliffMaskMode = on;
        Raise(nameof(LandCliffMaskMode)); Raise(nameof(CliffMaskLayerId));
        if (on) StatusDetail = "Маска обрыва: проведите кистью по берегу — «Стена» ставит обрыв, «Пляж» убирает его, «Стереть» возвращает как в стиле.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLandCliffMaskAction(CliffMaskAction action)
    {
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        LandCliffMaskAction = action;
        Raise(nameof(LandCliffMaskAction));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private string CliffMaskPreviewColor => LandCliffMaskAction switch
    {
        CliffMaskAction.Wall => "#C0B85C38",
        CliffMaskAction.Beach => "#C0E6CC7A",
        _ => "#A0FFFFFF",
    };

    /// <summary>The mask a stroke paints into, and the one it clears under itself.</summary>
    private (TileMap Primary, TileMap Companion) CliffMaskTargets(LandLayer land) => LandCliffMaskAction switch
    {
        CliffMaskAction.Beach => (land.CliffBeachTiles, land.CliffWallTiles),
        _ => (land.CliffWallTiles, land.CliffBeachTiles),
    };

    /// <summary>The land brush without material, colour or scatter: the mask is plain coverage.</summary>
    private BrushSettings CliffMaskSettings() => BrushSettings with
    {
        Texture = null, Eraser = false, Color = "#FFFFFFFF", Scatter = 0, SizeJitter = 0, OpacityJitter = 0,
    };

    /// <summary>One undoable step: «стена» and «пляж» add their own mask and clear the other; «стереть» clears both.</summary>
    private LandCliffMaskCommand CommitCliffMask(LayerId layerId, RasterStrokeSession stroke, RasterStrokeSession companion)
    {
        var erase = LandCliffMaskAction == CliffMaskAction.Erase;
        var (primaryBefore, primaryAfter) = stroke.CommitCoverage(missingTileValue: 0, subtract: erase);
        var (companionBefore, companionAfter) = companion.CommitCoverage(missingTileValue: 0, subtract: true);
        var name = LandCliffMaskAction switch
        {
            CliffMaskAction.Wall => "Маска обрыва: здесь стена",
            CliffMaskAction.Beach => "Маска обрыва: здесь пляж",
            _ => "Маска обрыва: стереть",
        };
        return LandCliffMaskAction == CliffMaskAction.Beach
            ? new LandCliffMaskCommand(layerId, companionBefore, companionAfter, primaryBefore, primaryAfter, name)
            : new LandCliffMaskCommand(layerId, primaryBefore, primaryAfter, companionBefore, companionAfter, name);
    }

    public bool ClearCliffMask()
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || !land.HasCliffMask) return false;
        static Dictionary<TileKey, byte[]?> Before(TileMap tiles) => tiles.Keys.ToDictionary(k => k, k => tiles.TryGet(k));
        static Dictionary<TileKey, byte[]?> Empty(TileMap tiles) => tiles.Keys.ToDictionary(k => k, _ => (byte[]?)null);
        Session.Execute(new LandCliffMaskCommand(land.Id, Before(land.CliffWallTiles), Empty(land.CliffWallTiles),
            Before(land.CliffBeachTiles), Empty(land.CliffBeachTiles), "Очистить маску обрыва"));
        AfterEdit(); return true;
    }
}
