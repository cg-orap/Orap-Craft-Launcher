using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orion.Core.Diagnostics;

namespace Orion.App.Services;

/// <summary>
/// Renders a Minecraft skin as a head avatar.
///
/// The skin PNG comes from the texture URL the profile API returned (Mojang's own CDN); the 8x8 face
/// is composed with the hat overlay and emitted as a tiny 8x8 bitmap that the UI scales up with
/// nearest-neighbour, which keeps the pixel art crisp. Composition is pure pixel arithmetic, so it
/// works on any thread (RenderTargetBitmap would require the UI thread).
/// Results are cached on disk so avatars also work offline afterwards.
/// </summary>
public static class SkinAvatars
{
    private const int FaceX = 8, FaceY = 8, HatX = 40, HatY = 8, Tile = 8, BytesPerPixel = 4;

    public static string CachePath(string root, string cacheKey)
    {
        var safe = new string(cacheKey.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(root, "cache", "skins", safe + ".png");
    }

    /// <summary>
    /// Returns the head image for a skin URL, or null when it cannot be produced (offline account,
    /// network failure, unreadable texture). Callers fall back to an initial badge.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(
        HttpClient http,
        string? skinUrl,
        string cacheKey,
        string root,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(skinUrl)) return null;

        var cachePath = CachePath(root, cacheKey);

        try
        {
            if (File.Exists(cachePath))
            {
                var cached = Decode(cachePath);
                if (cached is not null) return cached;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            AppLog.Warn("skin", "cache-read-failed", ("error", ex.Message));
        }

        try
        {
            // The profile API reports http://textures.minecraft.net/... which redirects to https.
            var uri = new Uri(skinUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase));
            var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);

            var head = CropHead(bytes);
            if (head is null)
            {
                AppLog.Warn("skin", "head-crop-failed", ("bytes", bytes.Length));
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await using var stream = File.Create(cachePath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create((BitmapSource)head));
                encoder.Save(stream);

                AppLog.Info("skin", "head-cached", ("key", cacheKey));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                // A cache write failure must not cost the user their avatar.
                AppLog.Warn("skin", "cache-write-failed", ("error", ex.Message));
            }

            return head;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException
                                       or NotSupportedException or TaskCanceledException)
        {
            AppLog.Warn("skin", "head-download-failed", ("error", ex.Message));
            return null;
        }
    }

    /// <summary>8x8 face with the hat overlay, from a 64x64 (or 64x32) skin texture.</summary>
    public static ImageSource? CropHead(byte[] pngBytes)
    {
        BitmapSource? texture;
        try
        {
            texture = Decode(pngBytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn("skin", "texture-decode-failed", ("type", ex.GetType().Name), ("error", ex.Message));
            return null;
        }

        if (texture is null)
        {
            AppLog.Warn("skin", "texture-decode-null");
            return null;
        }

        if (texture.PixelWidth < 64 || texture.PixelHeight < 32)
        {
            AppLog.Warn("skin", "texture-too-small", ("w", texture.PixelWidth), ("h", texture.PixelHeight));
            return null;
        }

        // Normalise to BGRA so the pixel copy below has a known layout.
        var converted = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * BytesPerPixel;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var head = new byte[Tile * Tile * BytesPerPixel];

        for (var y = 0; y < Tile; y++)
        {
            for (var x = 0; x < Tile; x++)
            {
                var face = Offset(pixels, stride, FaceX + x, FaceY + y);
                var hat = Offset(pixels, stride, HatX + x, HatY + y);
                var target = (y * Tile + x) * BytesPerPixel;

                // Start from the face, then alpha-blend the hat layer over it.
                head[target] = pixels[face];
                head[target + 1] = pixels[face + 1];
                head[target + 2] = pixels[face + 2];
                head[target + 3] = 255;

                BlendOver(pixels, hat, head, target);
            }
        }

        var result = BitmapSource.Create(Tile, Tile, 96, 96, PixelFormats.Bgra32, null, head, Tile * BytesPerPixel);
        result.Freeze();
        return result;
    }

    private static int Offset(byte[] pixels, int stride, int x, int y) => y * stride + x * BytesPerPixel;

    /// <summary>Source-over blend (BGRA, non-premultiplied) of one pixel onto the destination.</summary>
    private static void BlendOver(byte[] source, int sourceIndex, byte[] destination, int destinationIndex)
    {
        var alpha = source[sourceIndex + 3];
        if (alpha == 0) return;

        if (alpha == 255)
        {
            destination[destinationIndex] = source[sourceIndex];         // B
            destination[destinationIndex + 1] = source[sourceIndex + 1]; // G
            destination[destinationIndex + 2] = source[sourceIndex + 2]; // R
            destination[destinationIndex + 3] = 255;
            return;
        }

        var sa = alpha / 255.0;
        var da = destination[destinationIndex + 3] / 255.0;
        var outA = sa + da * (1 - sa);
        if (outA <= 0) return;

        for (var channel = 0; channel < 3; channel++)
        {
            var blended = (source[sourceIndex + channel] * sa
                           + destination[destinationIndex + channel] * da * (1 - sa)) / outA;
            destination[destinationIndex + channel] = (byte)Math.Clamp(blended, 0, 255);
        }

        destination[destinationIndex + 3] = (byte)Math.Clamp(outA * 255, 0, 255);
    }

    private static BitmapSource? Decode(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        // No IgnoreImageCache here: WPF builds that cache key from the URI, which a stream does not
        // have, and EndInit() would throw ArgumentNullException("key").
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Front-facing full-body figure built from the classic 64x64 skin UV map
    /// (head, body, both arms, both legs) - the same 2D preview mainstream launchers use.
    /// </summary>
    public static async Task<ImageSource?> LoadBodyAsync(
        HttpClient http,
        string? skinUrl,
        string cacheKey,
        string root,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(skinUrl)) return null;

        var cachePath = Path.Combine(root, "cache", "skins", cacheKey + "-body.png");

        try
        {
            if (File.Exists(cachePath))
            {
                var cached = Decode(cachePath);
                if (cached is not null) return cached;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            AppLog.Warn("skin", "body-cache-read-failed", ("error", ex.Message));
        }

        try
        {
            var uri = new Uri(skinUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase));
            var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);

            var body = ComposeBody(bytes);
            if (body is null) return null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await using var stream = File.Create(cachePath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create((BitmapSource)body));
                encoder.Save(stream);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                AppLog.Warn("skin", "body-cache-write-failed", ("error", ex.Message));
            }

            return body;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException
                                       or NotSupportedException or TaskCanceledException)
        {
            AppLog.Warn("skin", "body-download-failed", ("error", ex.Message));
            return null;
        }
    }

    /// <summary>Convenience wrapper: null instead of throwing when the body cannot be built.</summary>
    public static async Task<ImageSource?> LoadAsyncBodySafe(
        HttpClient http, string? skinUrl, string cacheKey, string root, CancellationToken ct = default)
    {
        try { return await LoadBodyAsync(http, skinUrl, cacheKey, root, ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("skin", "body-load-failed", ("error", ex.Message)); return null; }
    }

    /// <summary>16x32 unit figure (head, body, arms, legs) scaled 4x = 64x128 pixels.</summary>
    public static ImageSource? ComposeBody(byte[] pngBytes)
    {
        BitmapSource? texture;
        try { texture = Decode(pngBytes); }
        catch (Exception ex) { AppLog.Warn("skin", "texture-decode-failed", ("type", ex.GetType().Name)); return null; }

        if (texture is null || texture.PixelWidth < 64 || texture.PixelHeight < 64) return null;

        var converted = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        const int scaleUnits = 16;
        const int canvasW = scaleUnits * 4;   // 64 px
        const int canvasH = scaleUnits * 8;   // 128 px
        var canvas = new byte[canvasW * canvasH * 4];

        // (sourceX, sourceY, w, h, targetX, targetY) in skin units
        var parts = new (int Sx, int Sy, int W, int H, int Tx, int Ty)[]
        {
            (8, 8, 8, 8, 4, 0),      // head
            (20, 20, 8, 12, 4, 8),   // body
            (44, 20, 4, 12, 0, 8),   // right arm
            (36, 52, 4, 12, 12, 8),  // left arm
            (4, 20, 4, 12, 4, 20),   // right leg
            (20, 52, 4, 12, 8, 20),  // left leg
        };

        foreach (var part in parts)
        {
            for (var y = 0; y < part.H * 4; y++)
            {
                for (var x = 0; x < part.W * 4; x++)
                {
                    var sx = part.Sx + x / 4;
                    var sy = part.Sy + y / 4;
                    var src = sy * stride + sx * 4;

                    var dx = part.Tx * 4 + x;
                    var dy = part.Ty * 4 + y;
                    var dst = dy * canvasW * 4 + dx * 4;

                    canvas[dst] = pixels[src];
                    canvas[dst + 1] = pixels[src + 1];
                    canvas[dst + 2] = pixels[src + 2];
                    canvas[dst + 3] = pixels[src + 3] == 0 ? (byte)0 : (byte)255;
                }
            }
        }

        var result = BitmapSource.Create(canvasW, canvasH, 96, 96, PixelFormats.Bgra32, null, canvas, canvasW * 4);
        result.Freeze();
        return result;
    }
}
