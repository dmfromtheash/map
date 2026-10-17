using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Map.App.Controls;

/// <summary>Blue Atlas artwork with an explicit image size and a real transparent background.</summary>
public static class ApprovedArtwork
{
    private const string ResourceRoot = "avares://Map.App/Assets/Atlas/";

    private static readonly Dictionary<string, string> IconByToken = new()
    {
        [MapIcons.LibraryMaps] = "map",
        [MapIcons.Assets] = "map",
        [MapIcons.Select] = "select",
        [MapIcons.Line] = "line",
        [MapIcons.Shape] = "shape",
        [MapIcons.Text] = "text",
        [MapIcons.Notes] = "notes",
        [MapIcons.Brush] = "brush",
        [MapIcons.Mask] = "mask",
        [MapIcons.Land] = "land",
    };

    private static readonly Dictionary<string, Bitmap> Bitmaps = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    public static bool TryCreateIcon(string token, double requestedSize, out Control control)
    {
        // Secondary glyphs use MapIcons paths. Raster icons never grow past the requested slot.
        if (requestedSize < 24 || !IconByToken.TryGetValue(token, out var assetName))
        {
            control = null!;
            return false;
        }

        var opticalSize = assetName == "map" ? requestedSize * 0.9 : requestedSize;
        control = new Border
        {
            Width = requestedSize,
            Height = requestedSize,
            Child = Image(assetName, opticalSize, opticalSize),
            IsHitTestVisible = false,
        };
        return true;
    }

    public static Image Image(
        string assetName,
        double width,
        double height,
        Stretch stretch = Stretch.Uniform)
    {
        var image = new Image
        {
            Source = LoadBitmap(assetName),
            Width = width,
            Height = height,
            Stretch = stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    private static int _prewarmStarted;

    /// <summary>
    /// Decodes the tool rail artwork on a worker thread while the home screen is shown.
    /// </summary>
    /// <remarks>
    /// The first editor of a session otherwise decodes every rail icon inside its constructor
    /// (about 140 ms of the first open on the owner's PC). Decoding is behind the same lock as a
    /// normal load, so a concurrent load simply waits for or reuses the finished bitmap.
    /// </remarks>
    public static void PrewarmInBackground()
    {
        if (System.Threading.Interlocked.Exchange(ref _prewarmStarted, 1) != 0)
        {
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var assetName in new HashSet<string>(IconByToken.Values, StringComparer.Ordinal))
            {
                try { LoadBitmap(assetName); }
                catch (Exception) { return; } // The editor loads it again and reports a real failure.
            }
        });
    }

    private static Bitmap LoadBitmap(string assetName)
    {
        lock (Sync)
        {
            if (Bitmaps.TryGetValue(assetName, out var bitmap))
            {
                return bitmap;
            }

            using var stream = AssetLoader.Open(new Uri(ResourceRoot + assetName + ".png"));
            bitmap = Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
            Bitmaps[assetName] = bitmap;
            return bitmap;
        }
    }
}
