using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Map.App.Controls;

/// <summary>
/// The approved blue-theme artwork supplied by the owner.  The source boards are converted into
/// individual transparent PNG resources, so controls render the actual approved illustrations
/// instead of substituting small line glyphs.
/// </summary>
public static class ApprovedArtwork
{
    private const string ResourceRoot = "avares://Map.App/Assets/Approved/";

    private static readonly Dictionary<string, string> IconByToken = new()
    {
        [MapIcons.LibraryMaps] = "nav-library",
        [MapIcons.Assets] = "nav-assets-gallery",
        [MapIcons.Select] = "nav-select",
        [MapIcons.Line] = "nav-line",
        [MapIcons.Shape] = "nav-shape",
        [MapIcons.Text] = "nav-text",
        [MapIcons.ObjectStamp] = "nav-stamp",
        [MapIcons.Notes] = "nav-notes",
        [MapIcons.Brush] = "nav-brush",
        [MapIcons.Mask] = "nav-mask",
        [MapIcons.Land] = "nav-landscape",
        [MapIcons.Layers] = "nav-layers",
        [MapIcons.Objects] = "nav-objects",
        [MapIcons.LayerFilter] = "nav-filter",
        [MapIcons.LayerBase] = "nav-background",
        [MapIcons.ObjectComposition] = "nav-composition",

        [MapIcons.File] = "global-file",
        [MapIcons.Export] = "global-export",
        [MapIcons.Save] = "global-save",
        [MapIcons.Search] = "global-search",
        [MapIcons.Sort] = "global-sort",
        [MapIcons.NewMap] = "global-new-map",
        [MapIcons.Open] = "global-open-project",
        [MapIcons.Import] = "global-import",
        [MapIcons.Duplicate] = "global-copy",
        [MapIcons.Trash] = "global-delete",
        [MapIcons.Versions] = "global-versions",
        [MapIcons.ResizeRegion] = "global-resize-region",
        [MapIcons.Gear] = "global-settings",

        [MapIcons.BrushSize] = "property-brush-size",
        [MapIcons.Opacity] = "property-opacity",
        [MapIcons.SoftEdge] = "property-soft-edge",
        [MapIcons.RoughEdge] = "property-rough-edge",
        [MapIcons.Smoothing] = "property-smoothing",
        [MapIcons.ScaleObject] = "property-scale-object",
        [MapIcons.Rotate] = "property-rotate",
        [MapIcons.StampSpacing] = "property-stamp-spacing",
        [MapIcons.Color] = "property-color",
        [MapIcons.Texture] = "property-texture",
        [MapIcons.Eye] = "property-layer-visible",
        [MapIcons.Lock] = "property-layer-lock",
        [MapIcons.LayerOpacity] = "property-layer-opacity",
        [MapIcons.Blend] = "property-blend",
        [MapIcons.LayerMask] = "property-layer-mask",
        [MapIcons.LayerOrder] = "property-layer-order",
    };

    private static readonly Dictionary<string, Bitmap> Bitmaps = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    public static bool TryCreateIcon(string token, double requestedSize, out Control control)
    {
        if (!IconByToken.TryGetValue(token, out var assetName))
        {
            control = null!;
            return false;
        }

        // The approved illustrations contain watercolour shading and fine navy contours. They
        // need a little more room than a monochrome path with the same nominal size.
        var visualSize = requestedSize switch
        {
            <= 12 => requestedSize * 1.65,
            <= 18 => requestedSize * 2.0,
            <= 24 => requestedSize * 1.75,
            _ => requestedSize,
        };
        control = Image(assetName, visualSize, visualSize);
        return true;
    }

    public static Image Image(
        string assetName,
        double width,
        double height,
        Stretch stretch = Stretch.Uniform)
    {
        return new Image
        {
            Source = Bitmap(assetName),
            Width = width,
            Height = height,
            Stretch = stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    private static Bitmap Bitmap(string assetName)
    {
        lock (Sync)
        {
            if (Bitmaps.TryGetValue(assetName, out var bitmap))
            {
                return bitmap;
            }

            using var stream = AssetLoader.Open(new Uri(ResourceRoot + assetName + ".png"));
            bitmap = new Bitmap(stream);
            Bitmaps[assetName] = bitmap;
            return bitmap;
        }
    }
}
