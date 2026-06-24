using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace VoiceBridge.Desktop;

internal static class CompactStatusOverlayController
{
    private const int CompactWidth = 168;
    private const int CompactHeight = 42;
    private const string VisualizerName = "__VoiceBridgeCompactVisualizer";
    private static readonly System.Windows.Forms.Timer AnimationTimer = new() { Interval = 40 };
    private static bool _started;
    private static bool _applying;
    private static int _frame;

    [ModuleInitializer]
    public static void Initialize()
    {
        Application.Idle += StartAfterMessageLoopReady;
    }

    public static void TryApplyFromHandle(IntPtr handle)
    {
        if (_applying) return;
        if (Control.FromHandle(handle) is Form form && form.GetType().Name == "StatusOverlay")
        {
            ApplyCompactVisualizer(form, allowHidden: true);
        }
    }

    private static void StartAfterMessageLoopReady(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        Application.Idle -= StartAfterMessageLoopReady;
        Application.AddMessageFilter(new OverlayMessageFilter());
        AnimationTimer.Tick += (_, _) => TickVisibleOverlays();
        AnimationTimer.Start();
    }

    private static void TickVisibleOverlays()
    {
        _frame++;
        foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (form.GetType().Name == "StatusOverlay")
            {
                ApplyCompactVisualizer(form, allowHidden: false);
            }
        }
    }

    private static void ApplyCompactVisualizer(Form overlay, bool allowHidden)
    {
        if (_applying || overlay.IsDisposed) return;
        if (!allowHidden && !overlay.Visible) return;

        _applying = true;
        var labels = overlay.Controls.OfType<Label>().ToArray();
        var mode = NormalizeMode(
            string.Join(" ", labels.Select(x => x.Text ?? string.Empty)),
            CurrentMode(overlay));

        overlay.SuspendLayout();
        try
        {
            overlay.Width = CompactWidth;
            overlay.Height = CompactHeight;
            overlay.MinimumSize = new Size(CompactWidth, CompactHeight);
            overlay.MaximumSize = new Size(CompactWidth, CompactHeight);
            overlay.Padding = Padding.Empty;
            overlay.Opacity = 0.96;
            overlay.BackColor = Color.FromArgb(38, 38, 42);
            overlay.Location = CalculateCompactLocation();
            overlay.Region?.Dispose();
            overlay.Region = new Region(RoundedRect(new Rectangle(Point.Empty, overlay.ClientSize), CompactHeight / 2));

            foreach (var label in labels)
            {
                label.Visible = false;
                label.Dock = DockStyle.None;
                label.Bounds = Rectangle.Empty;
            }

            var visualizer = overlay.Controls.OfType<CompactOverlayVisualizer>().FirstOrDefault(x => x.Name == VisualizerName);
            if (visualizer == null)
            {
                visualizer = new CompactOverlayVisualizer
                {
                    Name = VisualizerName,
                    Dock = DockStyle.Fill
                };
                overlay.Controls.Add(visualizer);
            }

            visualizer.Mode = mode;
            visualizer.Frame = _frame;
            visualizer.BringToFront();
            visualizer.Invalidate();
        }
        finally
        {
            overlay.ResumeLayout(performLayout: false);
            _applying = false;
        }
    }

    private static CompactOverlayMode CurrentMode(Form overlay)
    {
        return overlay.Controls.OfType<CompactOverlayVisualizer>().FirstOrDefault(x => x.Name == VisualizerName)?.Mode
               ?? CompactOverlayMode.Working;
    }

    private static Point CalculateCompactLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Left + (area.Width - CompactWidth) / 2, area.Bottom - 92);
    }

    private static CompactOverlayMode NormalizeMode(string text, CompactOverlayMode fallback)
    {
        text = text ?? string.Empty;

        if (ContainsAny(text, "失败", "异常", "超时", "错误")) return CompactOverlayMode.Error;
        if (ContainsAny(text, "已取消", "取消")) return CompactOverlayMode.Cancel;
        if (ContainsAny(text, "已输入", "已重新粘贴", "完成", "成功", "设置已生效")) return CompactOverlayMode.Success;
        if (ContainsAny(text, "录音")) return CompactOverlayMode.Recording;
        if (ContainsAny(text, "输入", "粘贴", "请求模型", "解析", "后处理", "识别", "准备", "处理音频", "编码音频", "读取", "写入", "音频")) return CompactOverlayMode.Working;

        return fallback;
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class OverlayMessageFilter : IMessageFilter
    {
        private const int WmSize = 0x0005;
        private const int WmShowWindow = 0x0018;
        private const int WmWindowPosChanging = 0x0046;
        private const int WmWindowPosChanged = 0x0047;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is WmShowWindow or WmSize or WmWindowPosChanging or WmWindowPosChanged)
            {
                TryApplyFromHandle(m.HWnd);
            }

            return false;
        }
    }
}

internal enum CompactOverlayMode
{
    Recording,
    Working,
    Success,
    Error,
    Cancel
}

internal sealed class CompactOverlayVisualizer : Control
{
    private static readonly Color CapsuleBackColor = Color.FromArgb(38, 38, 42);

    public CompactOverlayMode Mode { get; set; } = CompactOverlayMode.Working;
    public int Frame { get; set; }

    public CompactOverlayVisualizer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = CapsuleBackColor;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(CapsuleBackColor);

        using var border = new Pen(Color.FromArgb(82, 255, 255, 255), 1f);
        using var borderPath = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        e.Graphics.DrawPath(border, borderPath);

        switch (Mode)
        {
            case CompactOverlayMode.Recording:
                DrawAudioBars(e.Graphics);
                break;
            case CompactOverlayMode.Success:
                DrawGlyph(e.Graphics, "✓");
                break;
            case CompactOverlayMode.Error:
                DrawGlyph(e.Graphics, "!");
                break;
            case CompactOverlayMode.Cancel:
                DrawGlyph(e.Graphics, "×");
                break;
            default:
                DrawPulseDots(e.Graphics);
                break;
        }
    }

    private void DrawAudioBars(Graphics g)
    {
        const int count = 11;
        const int barWidth = 5;
        const int gap = 5;
        var totalWidth = count * barWidth + (count - 1) * gap;
        var x = (Width - totalWidth) / 2;
        var centerY = Height / 2;

        using var brush = new SolidBrush(Color.FromArgb(232, 255, 255, 255));
        for (var i = 0; i < count; i++)
        {
            var wave = Math.Sin((Frame * 0.32) + i * 0.72);
            var height = 8 + (int)(Math.Abs(wave) * 22);
            var y = centerY - height / 2;
            using var path = RoundedRect(new Rectangle(x + i * (barWidth + gap), y, barWidth, height), barWidth);
            g.FillPath(brush, path);
        }
    }

    private void DrawPulseDots(Graphics g)
    {
        const int count = 3;
        const int gap = 18;
        var startX = Width / 2 - gap;
        var centerY = Height / 2;

        for (var i = 0; i < count; i++)
        {
            var phase = (Frame + i * 5) % 24;
            var pulse = 0.45 + 0.55 * Math.Sin(phase / 24.0 * Math.PI);
            var size = 7 + (int)(pulse * 5);
            var alpha = 120 + (int)(pulse * 110);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
            g.FillEllipse(brush, startX + i * gap - size / 2, centerY - size / 2, size, size);
        }
    }

    private void DrawGlyph(Graphics g, string glyph)
    {
        using var brush = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, 18, FontStyle.Bold);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, ClientRectangle, format);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
