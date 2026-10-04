namespace PeergosSnap.Core;

/// <summary>Pure parts of the drawing editor (unit tested): sizes, arrow heads, Shift-constraints and blur.</summary>
public static class AnnotateLogic
{
    /// <summary>Sizes grow a little with the picture, so a mark on a 4K screenshot is as visible as on a small one.</summary>
    static double Scale(int w, int h) => Math.Clamp(Math.Max(w, h) / 1600.0, 1, 2.5);

    public static double StrokeWidth(int size, int w, int h) => new[] { 3.0, 5.0, 8.0 }[Math.Clamp(size, 0, 2)] * Scale(w, h);
    public static double TextSize(int size, int w, int h) => new[] { 16.0, 22.0, 32.0 }[Math.Clamp(size, 0, 2)] * Scale(w, h);
    public static double NumberDiameter(int size, int w, int h) => new[] { 24.0, 32.0, 44.0 }[Math.Clamp(size, 0, 2)] * Scale(w, h);

    /// <summary>The block size of the pixelation: big enough that text under it cannot be read back. A quarter of the
    /// box's shorter side, so even large text dragged over closely is only a few blocks high.</summary>
    public static int BlurBlock(int size, int w, int h) =>
        Math.Clamp(Math.Min(w, h) / 4, new[] { 8, 12, 18 }[Math.Clamp(size, 0, 2)], 64);

    /// <summary>
    /// The arrow: where the shaft ends (under the head) and the two back corners of the head. The tip is at
    /// <paramref name="bx"/>,<paramref name="by"/>.
    /// </summary>
    public static ((double X, double Y) ShaftEnd, (double X, double Y) Left, (double X, double Y) Right) ArrowHead(
        double ax, double ay, double bx, double by, double width)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return ((bx, by), (bx, by), (bx, by));
        double ux = dx / len, uy = dy / len;
        double head = Math.Min(len * 0.6, Math.Max(12, width * 3.6));
        double half = head * 0.55;
        double baseX = bx - ux * head, baseY = by - uy * head;
        // The shaft stops inside the head, so its round end never pokes out of the tip.
        var shaft = (bx - ux * head * 0.6, by - uy * head * 0.6);
        return (shaft, (baseX - uy * half, baseY + ux * half), (baseX + uy * half, baseY - ux * half));
    }

    /// <summary>Shift while drawing: lines and arrows in steps of 45°, boxes and ellipses square / round.</summary>
    public static (double X, double Y) Constrain(double ax, double ay, double bx, double by, bool line)
    {
        double dx = bx - ax, dy = by - ay;
        if (line)
        {
            double len = Math.Sqrt(dx * dx + dy * dy);
            double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return (ax + Math.Cos(angle) * len, ay + Math.Sin(angle) * len);
        }
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return (ax + Math.Sign(dx == 0 ? 1 : dx) * side, ay + Math.Sign(dy == 0 ? 1 : dy) * side);
    }

    /// <summary>A dragged area in whole pixels, inside the picture.</summary>
    public static (int X, int Y, int W, int H) PixelRect(double ax, double ay, double bx, double by, int w, int h)
    {
        int x0 = (int)Math.Floor(Math.Clamp(Math.Min(ax, bx), 0, w)), y0 = (int)Math.Floor(Math.Clamp(Math.Min(ay, by), 0, h));
        int x1 = (int)Math.Ceiling(Math.Clamp(Math.Max(ax, bx), 0, w)), y1 = (int)Math.Ceiling(Math.Clamp(Math.Max(ay, by), 0, h));
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>White or black text, whichever reads better on the colour.</summary>
    public static bool DarkText(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b > 160;

    /// <summary>
    /// Pixelates an area of a Bgra32 picture: every block becomes the average of its pixels (alpha opaque). Returns
    /// the area alone (w × h × 4 bytes). Only the original pixels are used, so what was there cannot be read back.
    /// </summary>
    public static byte[] Pixelate(byte[] src, int width, int height, int x, int y, int w, int h, int block)
    {
        var dst = new byte[w * h * 4];
        block = Math.Max(2, block);
        for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
                long sb = 0, sg = 0, sr = 0, n = 0;
                for (int j = 0; j < bh; j++)
                {
                    int sy = y + by + j;
                    if (sy < 0 || sy >= height) continue;
                    for (int i = 0; i < bw; i++)
                    {
                        int sx = x + bx + i;
                        if (sx < 0 || sx >= width) continue;
                        int o = (sy * width + sx) * 4;
                        sb += src[o]; sg += src[o + 1]; sr += src[o + 2]; n++;
                    }
                }
                byte B = (byte)(n == 0 ? 0 : sb / n), G = (byte)(n == 0 ? 0 : sg / n), R = (byte)(n == 0 ? 0 : sr / n);
                for (int j = 0; j < bh; j++)
                    for (int i = 0; i < bw; i++)
                    {
                        int o = ((by + j) * w + bx + i) * 4;
                        dst[o] = B; dst[o + 1] = G; dst[o + 2] = R; dst[o + 3] = 255;
                    }
            }
        return dst;
    }
}
