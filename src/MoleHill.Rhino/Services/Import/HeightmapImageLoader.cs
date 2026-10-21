using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MoleHill.Core.Displacement;

// System.Drawing is Windows-only, and so is Rhino: the plug-in suppresses CA1416 project-wide, but the
// plain net8.0 test project compiles this file as linked source too.
#pragma warning disable CA1416

namespace MoleHill.Rhino.Services;

/// <summary>The identity of an image file on disk: a change to either means the file changed.</summary>
internal readonly record struct HeightmapFileStamp(long Length, long LastWriteUtcTicks);

/// <summary>
/// Reads a greyscale heightmap for Displace. The image is an external dependency of the terrain: the
/// definition keeps only its path, and the stage fingerprints the file's <see cref="HeightmapFileStamp"/>, so
/// saving over the image rebuilds the terrain. Decoding goes through System.Drawing (PNG, JPEG, BMP, GIF,
/// TIFF) to 8-bit luminance; one decode per file version is kept, so builds that find the file unchanged
/// never decode it again.
/// </summary>
internal static class HeightmapImageLoader
{
    /// <summary>The largest image read (16 megapixels, 64 MB of samples).</summary>
    public const long MaxPixels = 16L * 1024 * 1024;

    private const int CacheCapacity = 4;

    private static readonly object Gate = new();
    private static readonly List<(string Path, HeightmapFileStamp Stamp, HeightmapImage Image)> Cache = new();

    /// <summary>The image files the browse dialog offers.</summary>
    public static IReadOnlyList<string> Extensions { get; } = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>The file's stamp, or why it cannot be read.</summary>
    public static bool TryGetStamp(string? path, out string fullPath, out HeightmapFileStamp stamp, out string? error)
    {
        fullPath = string.Empty;
        stamp = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose an image file to displace by.";
            return false;
        }

        try
        {
            if (!Path.IsPathRooted(path))
            {
                error = $"Give the image's full path; '{path}' is relative.";
                return false;
            }

            fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                error = $"The image file '{Path.GetFileName(fullPath)}' was not found at {Path.GetDirectoryName(fullPath)}.";
                return false;
            }

            stamp = new HeightmapFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"The image path cannot be read: {ex.Message}";
            return false;
        }
    }

    /// <summary>The decoded image at <paramref name="path"/>, from the cache when the file is unchanged.</summary>
    public static bool TryLoad(string? path, out HeightmapImage? image, out HeightmapFileStamp stamp, out string? error)
    {
        image = null;
        if (!TryGetStamp(path, out string fullPath, out stamp, out error))
            return false;

        lock (Gate)
        {
            foreach ((string cachedPath, HeightmapFileStamp cachedStamp, HeightmapImage cachedImage) in Cache)
            {
                if (cachedStamp == stamp && string.Equals(cachedPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    image = cachedImage;
                    return true;
                }
            }
        }

        try
        {
            // Read the bytes first: a Bitmap opened on the path would hold the file locked for its lifetime.
            byte[] bytes = File.ReadAllBytes(fullPath);
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            if ((long)bitmap.Width * bitmap.Height > MaxPixels)
            {
                error = $"The image is {bitmap.Width:N0} x {bitmap.Height:N0} pixels; Displace reads up to {MaxPixels / (1024 * 1024)} megapixels. Use a smaller image.";
                return false;
            }

            image = Decode(bitmap);
        }
        catch (Exception ex)
        {
            // GDI+ reports an unreadable image as OutOfMemoryException or ArgumentException; neither is fatal here.
            error = $"The image file '{Path.GetFileName(fullPath)}' could not be read as an image ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }

        lock (Gate)
        {
            Cache.RemoveAll(entry => string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            Cache.Add((fullPath, stamp, image));
            if (Cache.Count > CacheCapacity)
                Cache.RemoveAt(0);
        }

        return true;
    }

    /// <summary>The bitmap's Rec. 709 luminance (a grey pixel's own value), 0 (black) to 1 (white), row 0 at the top. Alpha is ignored.</summary>
    internal static HeightmapImage Decode(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var values = new float[(long)width * height];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                for (int x = 0; x < width; x++)
                {
                    // Format32bppArgb is B, G, R, A in memory.
                    byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    double luminance = r == g && g == b ? r : (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    values[(y * width) + x] = (float)(luminance / 255.0);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new HeightmapImage(width, height, values);
    }
}
