using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Turns pasted/dropped images into <see cref="PendingImageAttachment"/>s: decodes
/// (any format WPF can read), downscales to Claude's recommended max edge, and
/// re-encodes to PNG — falling back to JPEG when the PNG would exceed the size the
/// API accepts. Output media type is therefore always one Claude supports
/// (<c>image/png</c> or <c>image/jpeg</c>), regardless of the source format.
/// </summary>
public static class ImageAttachmentHelper
{
    // Anthropic resizes anything larger than ~1568px on the long edge, so there's
    // no benefit to sending bigger — downscale first to keep payloads small.
    private const int MaxEdge = 1568;

    // The API caps a base64 image at ~5 MB; base64 inflates by ~4/3, so keep the
    // raw encoded bytes under ~3.6 MB. PNGs of photos blow past this, hence the
    // JPEG fallback.
    private const long MaxRawBytes = 3_600_000;

    // Long edge of the in-app thumbnail kept on the VM (chip + message bubble).
    private const int ThumbnailEdge = 200;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };

    public static bool IsSupportedImageFile(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Decodes the (already downscaled/normalized) attachment bytes into a frozen
    /// bitmap for the enlarged lightbox preview. Falls back to the small thumbnail
    /// if decoding somehow fails.
    /// </summary>
    public static ImageSource CreatePreview(PendingImageAttachment attachment)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(attachment.Data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"ImageAttachment: failed to decode preview — {ex.Message}");
            return attachment.Thumbnail;
        }
    }

    /// <summary>
    /// Builds an attachment from an image file on disk. Returns null (and logs) if
    /// the file can't be decoded.
    /// </summary>
    public static PendingImageAttachment? FromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            return Build(decoder.Frames[0], Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"ImageAttachment: failed to load '{path}' — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds an attachment from whatever image is currently on the clipboard,
    /// preferring the raw <c>PNG</c> stream (what the Windows Snipping Tool, browsers,
    /// and most editors place there) over the CF_DIB / CF_BITMAP that
    /// <see cref="Clipboard.GetImage"/> reads — the DIB path drops the alpha channel
    /// and renders screenshots with a black background on some sources. Returns null
    /// (and logs) if the clipboard has no usable image.
    /// </summary>
    public static PendingImageAttachment? FromClipboard()
    {
        try
        {
            // Prefer PNG: decode the stream directly so transparency survives.
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png && png.Length > 0)
            {
                png.Position = 0;
                var decoder = BitmapDecoder.Create(png,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                    return Build(decoder.Frames[0], "Pasted image");
            }

            // Fall back to the standard bitmap accessor (DIB / CF_BITMAP).
            if (Clipboard.ContainsImage())
            {
                var bmp = Clipboard.GetImage();
                if (bmp != null)
                    return Build(bmp, "Pasted image");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ImageAttachment: failed to read clipboard image — {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Builds an attachment from an in-memory bitmap (e.g. a pasted screenshot).
    /// Returns null (and logs) on failure.
    /// </summary>
    public static PendingImageAttachment? FromBitmap(BitmapSource source, string displayName)
    {
        try
        {
            return Build(source, displayName);
        }
        catch (Exception ex)
        {
            Log.Warn($"ImageAttachment: failed to process bitmap — {ex.Message}");
            return null;
        }
    }

    private static PendingImageAttachment Build(BitmapSource source, string displayName)
    {
        var scaled = Downscale(source, MaxEdge);

        // Prefer PNG (lossless); fall back to JPEG when it's too large to send.
        var (data, mediaType) = Encode(scaled);

        var thumbnail = Downscale(source, ThumbnailEdge);
        thumbnail.Freeze();

        return new PendingImageAttachment
        {
            Thumbnail = thumbnail,
            DisplayName = displayName,
            MediaType = mediaType,
            Data = data
        };
    }

    private static (byte[] Data, string MediaType) Encode(BitmapSource image)
    {
        var png = EncodeWith(new PngBitmapEncoder(), image);
        if (png.LongLength <= MaxRawBytes)
            return (png, "image/png");

        var jpeg = EncodeWith(new JpegBitmapEncoder { QualityLevel = 85 }, image);
        Log.Info($"ImageAttachment: PNG was {png.LongLength} bytes (> {MaxRawBytes}); using JPEG ({jpeg.LongLength} bytes)");
        return (jpeg, "image/jpeg");
    }

    private static byte[] EncodeWith(BitmapEncoder encoder, BitmapSource image)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Returns <paramref name="source"/> scaled down so its longest edge is at most
    /// <paramref name="maxEdge"/>. Images already within bounds are returned as-is.
    /// </summary>
    private static BitmapSource Downscale(BitmapSource source, int maxEdge)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxEdge)
            return source;

        var scale = (double)maxEdge / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        return scaled;
    }
}
