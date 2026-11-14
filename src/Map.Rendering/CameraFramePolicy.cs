using Map.Core.Documents;

namespace Map.Rendering;

/// <summary>
/// When the editor canvas may stand a kept camera frame (<see cref="CameraFrameCache"/>) in for a
/// moving view. The frame holds the scene alone: the selection outline and handles, the marquee
/// and the brush cursor are drawn over it every frame (<see cref="SceneRenderer.DrawEditorOverlay"/>),
/// so a selection or an armed brush does not stop it. Anything that changes the scene itself from
/// one frame to the next (a drag, a stroke, a placement, a hex gesture, a live river or road) does.
/// </summary>
public static class CameraFramePolicy
{
    /// <summary>What the canvas knows about the frame it is about to draw.</summary>
    public readonly record struct Inputs(
        bool Enabled,
        bool GpuSurface,
        bool LiveObjectPreview,
        bool StrokePreview,
        bool CanvasWorkflowOpen,
        bool Marquee,
        bool FogStroke,
        bool HexGesture,
        bool HexCursor,
        bool SeamlessPlanet,
        bool SceneOverrides,
        bool Selection,
        bool BrushCursor);

    /// <summary>
    /// True when the kept frame may be used and captured. <see cref="Inputs.Selection"/> and
    /// <see cref="Inputs.BrushCursor"/> are screen decorations over the scene and do not count.
    /// </summary>
    public static bool Eligible(in Inputs inputs) =>
        inputs.Enabled && inputs.GpuSurface
        && !inputs.LiveObjectPreview && !inputs.StrokePreview && !inputs.CanvasWorkflowOpen
        && !inputs.Marquee && !inputs.FogStroke && !inputs.HexGesture && !inputs.HexCursor
        && !inputs.SeamlessPlanet && !inputs.SceneOverrides;

    /// <summary>
    /// The selection as a value for the frame's identity. The scene itself depends on it a little
    /// (a selected label always shows; a selected object stays exact over the far-zoom summary), so
    /// a kept frame is dropped when the selection changes. The session's list is changed in place,
    /// hence a value: the primary, the count and a hash of the ids in order.
    /// </summary>
    public static SelectionKey KeyOf(ObjectId? primary, IReadOnlyList<ObjectId> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var hash = new HashCode();
        for (var i = 0; i < selection.Count; i++) hash.Add(selection[i]);
        return new SelectionKey(primary, selection.Count, hash.ToHashCode());
    }

    /// <summary>A selection by value, see <see cref="KeyOf"/>.</summary>
    public readonly record struct SelectionKey(ObjectId? Primary, int Count, int Hash);
}
