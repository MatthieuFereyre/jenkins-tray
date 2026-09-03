using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using JenkinsTray.Models;
using MediaColor = System.Windows.Media.Color;

namespace JenkinsTray.Services;

/// <summary>
/// Composes the notification-area glyph: the application artwork with a status dot pinned to its
/// bottom-right corner. Drawn with GDI+ rather than WPF because the tray wants a real
/// <see cref="Icon"/> handle, and the same drawing also bakes the PNG the toast app-logo needs.
/// </summary>
public static class IconRenderer
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<BuildStatus, string> PngCache = [];

    private static Bitmap? _artwork;
    private static bool _artworkUnavailable;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static MediaColor ColorFor(BuildStatus status) => status switch
    {
        BuildStatus.Success => MediaColor.FromRgb(0x2E, 0xA0, 0x43),
        BuildStatus.Unstable => MediaColor.FromRgb(0xE0, 0xA0, 0x0B),
        BuildStatus.Failure => MediaColor.FromRgb(0xD4, 0x35, 0x3D),
        BuildStatus.Aborted => MediaColor.FromRgb(0x8B, 0x94, 0x9E),
        BuildStatus.NotBuilt => MediaColor.FromRgb(0x6E, 0x77, 0x81),
        BuildStatus.Disabled => MediaColor.FromRgb(0x57, 0x5E, 0x66),
        _ => MediaColor.FromRgb(0x00, 0x78, 0xD4),
    };

    /// <summary>
    /// Tray icon for a status; <paramref name="opacity"/> is used to pulse while building.
    /// A brand new instance is returned every time on purpose: the tray control takes ownership of
    /// the icon it is given and disposes it, so a shared cached instance would come back dead.
    /// The caller owns the result.
    /// </summary>
    public static Icon CreateTrayIcon(BuildStatus status, double opacity = 1.0, int size = 32)
    {
        using var bitmap = Render(status, size, opacity);

        var handle = bitmap.GetHicon();
        try
        {
            // FromHandle does not take ownership, so clone before releasing the handle.
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Path to a 96 px PNG of the status glyph, rendered once then reused by the toasts.</summary>
    public static string? GetStatusPngPath(BuildStatus status)
    {
        lock (Gate)
        {
            if (PngCache.TryGetValue(status, out var cached) && File.Exists(cached))
                return cached;
        }

        try
        {
            var directory = Path.Combine(SettingsStore.DataDirectory, "icons");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"status-{status.ToString().ToLowerInvariant()}.png");

            using (var bitmap = Render(status, 96, 1.0))
                bitmap.Save(path, ImageFormat.Png);

            lock (Gate)
                PngCache[status] = path;

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>Renders the composite glyph. Public so a harness can inspect it at any size.</summary>
    public static Bitmap Render(BuildStatus status, int size, double opacity)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var artwork = GetArtwork();
        if (artwork is not null)
            DrawArtwork(graphics, artwork, size, opacity);
        else
            DrawFallbackDisc(graphics, status, size, opacity);

        // Unknown means "nothing monitored yet" — the plain logo reads better than a grey dot.
        if (status != BuildStatus.Unknown)
            DrawStatusBadge(graphics, status, size, opacity);

        return bitmap;
    }

    private static void DrawArtwork(Graphics graphics, Bitmap artwork, int size, double opacity)
    {
        var scale = Math.Min((float)size / artwork.Width, (float)size / artwork.Height);
        var width = artwork.Width * scale;
        var height = artwork.Height * scale;

        var destination = new Rectangle(
            (int)Math.Round((size - width) / 2),
            (int)Math.Round((size - height) / 2),
            (int)Math.Round(width),
            (int)Math.Round(height));

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacity });

        graphics.DrawImage(
            artwork, destination, 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>A white ring detaches the dot from the artwork it sits on.</summary>
    private static void DrawStatusBadge(Graphics graphics, BuildStatus status, int size, double opacity)
    {
        var alpha = (int)Math.Clamp(Math.Round(255 * opacity), 0, 255);
        var diameter = size * 0.52f;
        var margin = size * 0.02f;
        var left = size - diameter - margin;
        var top = size - diameter - margin;

        using var ring = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        graphics.FillEllipse(ring, left, top, diameter, diameter);

        var inset = Math.Max(1f, size * 0.06f);
        var color = ColorFor(status);

        using var fill = new SolidBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        graphics.FillEllipse(fill, left + inset, top + inset, diameter - 2 * inset, diameter - 2 * inset);
    }

    /// <summary>Used only if the embedded artwork cannot be loaded, so the tray is never blank.</summary>
    private static void DrawFallbackDisc(Graphics graphics, BuildStatus status, int size, double opacity)
    {
        var alpha = (int)Math.Clamp(Math.Round(255 * opacity), 0, 255);
        var color = ColorFor(status);
        var inset = Math.Max(1f, size * 0.03f);

        using var brush = new SolidBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        graphics.FillEllipse(brush, inset, inset, size - 2 * inset, size - 2 * inset);
    }

    private static Bitmap? GetArtwork()
    {
        lock (Gate)
        {
            if (_artwork is not null || _artworkUnavailable)
                return _artwork;

            try
            {
                // Assembly-qualified: a plain "/Assets/icon.png" would resolve against whichever
                // assembly happens to be the entry point.
                var uri = new Uri("pack://application:,,,/JenkinsTray;component/Assets/icon.png", UriKind.Absolute);
                var resource = Application.GetResourceStream(uri);

                if (resource is null)
                {
                    _artworkUnavailable = true;
                    return null;
                }

                using var stream = resource.Stream;
                using var decoded = new Bitmap(stream);

                // Copy so the bitmap no longer depends on the stream we are about to close.
                _artwork = new Bitmap(decoded);
                return _artwork;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or ExternalException)
            {
                AppLog.Warn("Loading the icon artwork", ex);
                _artworkUnavailable = true;
                return null;
            }
        }
    }
}
