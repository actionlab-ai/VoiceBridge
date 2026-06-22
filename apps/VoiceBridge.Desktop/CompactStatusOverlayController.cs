using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace VoiceBridge.Desktop;

internal static class CompactStatusOverlayController
{
    private const int CompactWidth = 260;
    private const int CompactHeight = 72;
    private static readonly System.Windows.Forms.Timer AnimationTimer = new() { Interval = 260 };
    private static bool _started;
    private static int _frame;

    [ModuleInitializer]
    public static void Initialize()
    {
        Application.Idle += StartAfterMessageLoopReady;
    }

    private static void StartAfterMessageLoopReady(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        Application.Idle -= StartAfterMessageLoopReady;
        AnimationTimer.Tick += (_, _) => Tick();
        AnimationTimer.Start();
    }

    private static void Tick()
    {
        _frame++;
        foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (form.GetType().Name == "StatusOverlay")
            {
                ApplyCompactStatus(form);
            }
        }
    }

    private static void ApplyCompactStatus(Form overlay)
    {
        if (overlay.IsDisposed || !overlay.Visible) return;

        overlay.Width = CompactWidth;
        overlay.Height = CompactHeight;
        overlay.Padding = new Padding(14, 10, 14, 10);
        overlay.Opacity = 0.92;
        overlay.BackColor = Color.FromArgb(32, 32, 36);
        overlay.Location = CalculateCompactLocation();

        var labels = overlay.Controls.OfType<Label>().ToArray();
        var titleLabel = labels.FirstOrDefault(x => x.Dock == DockStyle.Top);
        var detailLabel = labels.FirstOrDefault(x => x.Dock == DockStyle.Fill);
        if (titleLabel == null || detailLabel == null) return;

        var state = NormalizeState(titleLabel.Text, detailLabel.Text);
        titleLabel.Height = 30;
        titleLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11, FontStyle.Bold);
        titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        titleLabel.Text = $"{IconForState(state)} {state}";

        detailLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10, FontStyle.Regular);
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;
        detailLabel.Text = DetailForState(state);
        overlay.Invalidate();
    }

    private static Point CalculateCompactLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Left + (area.Width - CompactWidth) / 2, area.Bottom - 120);
    }

    private static string NormalizeState(string title, string detail)
    {
        var text = ((title ?? string.Empty) + " " + (detail ?? string.Empty)).Trim();

        if (ContainsAny(text, "失败", "异常", "超时", "错误")) return "异常";
        if (ContainsAny(text, "已取消", "取消")) return "取消";
        if (ContainsAny(text, "已输入", "已重新粘贴", "完成", "成功", "设置已生效")) return "完成";
        if (ContainsAny(text, "正在输入", "粘贴", "当前窗口")) return "粘贴";
        if (ContainsAny(text, "请求模型", "解析结果", "后处理", "识别中", "模型已返回", "ASR 返回")) return "解析";
        if (ContainsAny(text, "准备", "处理音频", "编码音频", "读取 wav", "写入 wav", "音频")) return "读取";
        if (ContainsAny(text, "录音")) return "录音";

        return "处理中";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string IconForState(string state) => state switch
    {
        "录音" => "●",
        "完成" => "✓",
        "异常" => "!",
        "取消" => "i",
        _ => "…"
    };

    private static string DetailForState(string state) => state switch
    {
        "完成" => string.Empty,
        "异常" => "查看日志",
        "取消" => string.Empty,
        _ => AnimatedDots()
    };

    private static string AnimatedDots()
    {
        return (_frame % 4) switch
        {
            0 => "·",
            1 => "··",
            2 => "···",
            _ => "··"
        };
    }
}
