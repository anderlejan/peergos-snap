using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

public static class ScreenCapture
{
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    /// <summary>Copies a region of the live screen (physical pixels). Includes layered windows (tooltips, menus).</summary>
    public static Bitmap Grab(PxRect r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        var dst = g.GetHdc();
        var src = GetDC(IntPtr.Zero);
        try
        {
            if (!BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("The screen could not be copied");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dst);
        }
        return bmp;
    }

    public static void Save(Bitmap bmp, string file, string format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (format == "jpg")
        {
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            bmp.Save(file, enc, p);
        }
        else bmp.Save(file, ImageFormat.Png);
    }
}
