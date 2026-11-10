using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace CheatingTool;

internal static class ScreenCapture
{
    public static byte[] CaptureJpeg(CaptureConfig cfg)
    {
        var monitor = cfg.Monitor == "primary" ? Displays.Primary() : Displays.UnderCursor();

        int width = monitor.Width;
        int height = monitor.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("не удалось определить размер экрана");

        using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        int targetWidth = Math.Min(cfg.MaxWidth, width);
        int targetHeight = (int)Math.Round(height * (double)targetWidth / width);
        using var ms = new MemoryStream();

        if (targetWidth == width)
        {
            SaveJpeg(full, ms, cfg.JpegQuality);
        }
        else
        {
            using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, targetWidth, targetHeight));
            }
            SaveJpeg(scaled, ms, cfg.JpegQuality);
        }

        return ms.ToArray();
    }

    private static void SaveJpeg(Bitmap bitmap, Stream stream, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bitmap.Save(stream, codec, parameters);
    }
}
