using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PeergosSnap.Core;
using WinForms = System.Windows.Forms;

namespace PeergosSnap.UI;

/// <summary>Paints the tray menu in the app's colour scheme and font size, with rounded Windows 11 corners.</summary>
public static class ThemedMenu
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static void Apply(WinForms.ContextMenuStrip menu)
    {
        var s = Theme.Current;
        menu.Renderer = new Renderer(s);
        menu.BackColor = C(s.Bg2);
        menu.ForeColor = C(s.Fg);
        menu.Font = new Font("Segoe UI", (float)(9.0 * Theme.FontScale), FontStyle.Regular, GraphicsUnit.Point);
        menu.ShowImageMargin = true;
        menu.Padding = new WinForms.Padding(2, 4, 2, 4);
        if (menu.Tag as string != "rounded")
        {
            menu.Tag = "rounded";
            menu.Opened += (_, _) => Round(menu.Handle);
        }
    }

    static void Round(IntPtr hwnd)
    {
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
    }

    sealed class Colors(Scheme s) : WinForms.ProfessionalColorTable
    {
        readonly Color bg = C(s.Bg2), line = C(s.Line), hover = Theme.Luminance(s.Bg2) < 0.3
            ? WinForms.ControlPaint.Light(C(s.Bg2), 0.25f) : WinForms.ControlPaint.Dark(C(s.Bg2), 0.06f);
        public override Color ToolStripDropDownBackground => bg;
        public override Color ImageMarginGradientBegin => bg;
        public override Color ImageMarginGradientMiddle => bg;
        public override Color ImageMarginGradientEnd => bg;
        public override Color MenuBorder => line;
        public override Color MenuItemBorder => hover;
        public override Color MenuItemSelected => hover;
        public override Color MenuItemSelectedGradientBegin => hover;
        public override Color MenuItemSelectedGradientEnd => hover;
        public override Color SeparatorDark => line;
        public override Color SeparatorLight => line;
        public override Color CheckBackground => bg;
        public override Color CheckSelectedBackground => hover;
        public override Color CheckPressedBackground => hover;
    }

    sealed class Renderer(Scheme s) : WinForms.ToolStripProfessionalRenderer(new Colors(s))
    {
        readonly Color fg = C(s.Fg), fg3 = C(s.Fg3), acc = C(s.Acc), line = C(s.Line);

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item is WinForms.ToolStripLabel || !e.Item.Enabled ? fg3 : fg;
            if (e.Item is WinForms.ToolStripLabel) e.TextFont = new Font(e.TextFont ?? e.Item.Font, FontStyle.Bold);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = fg;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            // An accent-coloured tick instead of the classic boxed one.
            var r = e.ImageRectangle;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1.6f, r.Height / 9f);
            using var pen = new Pen(acc, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var p1 = new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f);
            var p2 = new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f);
            var p3 = new PointF(r.Left + r.Width * 0.84f, r.Top + r.Height * 0.26f);
            g.DrawLines(pen, [p1, p2, p3]);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            var r = e.Item.ContentRectangle;
            using var pen = new Pen(line);
            int y = r.Top + r.Height / 2;
            e.Graphics.DrawLine(pen, r.Left + 8, y, r.Right - 8, y);
        }
    }
}
