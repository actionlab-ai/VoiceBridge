using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;

namespace VoiceBridge.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show("VoiceBridge 运行异常：" + e.Exception.Message, "VoiceBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) MessageBox.Show("VoiceBridge 运行异常：" + ex.Message, "VoiceBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        using var context = new TrayAppContext();
        Application.Run(context);
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon;
    private readonly AppLogger _logger;
    private readonly HistoryStore _history;
    private readonly GlobalKeyboardHook _keyboard;
    private readonly StatusOverlay _overlay;
    private AudioRecorder? _recorder;
    private CancellationTokenSource? _recognitionCts;
    private bool _recording;
    private bool _recognizing;
    private string _lastText = string.Empty;

    public TrayAppContext()
    {
        _config = ConfigStore.Load();
        _config.Endpoint = EndpointUtil.NormalizeBaseEndpoint(_config.Endpoint);
        _logger = new AppLogger();
        _history = new HistoryStore();
        _appIcon = AppIconFactory.CreateIcon();
        _overlay = new StatusOverlay();
        StartupManager.SetEnabled(_config.AutoStart);

        _tray = new NotifyIcon
        {
            Text = "VoiceBridge 语音桥",
            Icon = _appIcon,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.ShowBalloonTip(2500, "VoiceBridge 已启动", $"双击托盘打开设置；按住 {_config.HoldKey} 开始语音输入。", ToolTipIcon.Info);

        _keyboard = new GlobalKeyboardHook();
        _keyboard.KeyDown += HandleKeyDown;
        _keyboard.KeyUp += HandleKeyUp;
        _keyboard.Start();
        _logger.Info("VoiceBridge started.");
        _logger.Info($"Runtime config: endpoint={_config.Endpoint}, model={_config.ModelName}, timeout={_config.TimeoutSeconds}s, hotkey={_config.HoldKey}");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("识别历史", null, (_, _) => new HistoryForm(_history).Show());
        menu.Items.Add("日志查看", null, (_, _) => new LogForm(_logger.LogPath).Show());
        menu.Items.Add("重新粘贴上次结果", null, async (_, _) => await PasteLastAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowSettings()
    {
        try
        {
            using var form = new SettingsForm(_config);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _config.Endpoint = EndpointUtil.NormalizeBaseEndpoint(_config.Endpoint);
                ConfigStore.Save(_config);
                ApplyRuntimeSettings(showTip: true);
                _logger.Info($"Settings saved and applied from UI. endpoint={_config.Endpoint}, model={_config.ModelName}, timeout={_config.TimeoutSeconds}s, hotkey={_config.HoldKey}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open settings", ex);
            MessageBox.Show("打开设置失败：" + ex.Message, "VoiceBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyRuntimeSettings(bool showTip)
    {
        StartupManager.SetEnabled(_config.AutoStart);
        _tray.Text = "VoiceBridge 语音桥";
        _tray.ContextMenuStrip = BuildMenu();
        if (showTip)
        {
            _tray.ShowBalloonTip(1800, "设置已生效", $"热键：{_config.HoldKey}；模型：{_config.ModelName}", ToolTipIcon.Info);
            ShowOverlay("设置已生效", $"热键 {_config.HoldKey} · 模型 {_config.ModelName}", OverlayKind.Success, 1600);
        }
    }

    private void HandleKeyDown(Keys key)
    {
        if (key == _config.HoldKey)
        {
            if (_recording) return;
            if (_recognizing)
            {
                ShowOverlay("正在识别中", "模型还在处理上一段语音，请稍等或按 Esc 取消。", OverlayKind.Recognizing, 1600);
                _logger.Info("Ignored hotkey because recognition is still running.");
                return;
            }

            BeginRecord();
            return;
        }

        if (key == Keys.Escape)
        {
            if (_recording)
            {
                CancelRecord();
                return;
            }

            if (_recognizing)
            {
                _recognitionCts?.Cancel();
                ShowOverlay("正在取消", "已发送取消信号。", OverlayKind.Info, 1200);
                _logger.Info("Recognition cancellation requested by Esc.");
                return;
            }
        }

        if (key == Keys.F9 && !_recording && !_recognizing)
        {
            _ = PasteLastAsync();
            return;
        }

        if (key == Keys.R && GlobalKeyboardHook.IsCtrlAltDown())
        {
            InputInjector.ReleaseModifiers();
            _logger.Info("Released modifier keys.");
            ShowOverlay("已释放修饰键", "Ctrl / Alt / Win / Shift 已释放", OverlayKind.Info, 1200);
        }
    }

    private void HandleKeyUp(Keys key)
    {
        if (key == _config.HoldKey && _recording)
        {
            _ = StopAndTranscribeAsync();
        }
    }

    private void BeginRecord()
    {
        try
        {
            _recording = true;
            _recorder = new AudioRecorder(_config.MicrophoneDeviceNumber, _config.SampleRate, _config.Channels, _logger);
            _recorder.Start();
            _tray.Text = "VoiceBridge 正在录音...";
            ShowOverlay("正在录音", $"松开 {_config.HoldKey} 后发送给 {_config.ModelName}", OverlayKind.Recording);
            _logger.Info($"Recording started. device={_config.MicrophoneDeviceNumber}, sampleRate={_config.SampleRate}, channels={_config.Channels}");
        }
        catch (Exception ex)
        {
            _recording = false;
            _logger.Error("Failed to start recording", ex);
            ShowOverlay("录音启动失败", ex.Message, OverlayKind.Error, 3000);
            _tray.ShowBalloonTip(2000, "VoiceBridge", "录音启动失败：" + ex.Message, ToolTipIcon.Error);
        }
    }

    private void CancelRecord()
    {
        try
        {
            _recorder?.Cancel();
            _recording = false;
            _tray.Text = "VoiceBridge 语音桥";
            ShowOverlay("已取消", "本次语音输入已取消", OverlayKind.Info, 1200);
            _logger.Info("Recording cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error("Cancel failed", ex);
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        var total = Stopwatch.StartNew();
        var phase = new RecognitionProgress("正在准备", "停止录音并准备音频", OverlayKind.Recognizing);
        using var progressTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        try
        {
            _recording = false;
            _recognizing = true;
            _recognitionCts = new CancellationTokenSource();
            _tray.Text = "VoiceBridge 正在识别...";

            progressTimer.Tick += (_, _) => ShowOverlay(phase.Title, WithElapsed(phase.Detail, total), phase.Kind);
            progressTimer.Start();

            var progress = new Progress<RecognitionProgress>(p =>
            {
                phase = p;
                ShowOverlay(p.Title, WithElapsed(p.Detail, total), p.Kind, p.AutoHideMs);
            });

            ((IProgress<RecognitionProgress>)progress).Report(new RecognitionProgress("正在处理音频", "录音已结束，正在写入 wav 文件", OverlayKind.Recognizing));
            var wav = await (_recorder?.StopAsync() ?? Task.FromResult(string.Empty));
            if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
            {
                ShowOverlay("没有录音文件", "没有可识别的音频", OverlayKind.Info, 1800);
                return;
            }

            var duration = AudioRecorder.TryGetDuration(wav);
            _logger.Info($"Recording stopped. file={wav}, duration={duration.TotalSeconds:F2}s, size={FileUtil.FormatBytes(new FileInfo(wav).Length)}");

            var client = new AsrClient(_config, _logger);
            var text = await client.TranscribeAsync(wav, progress, _recognitionCts.Token);
            ((IProgress<RecognitionProgress>)progress).Report(new RecognitionProgress("正在后处理", "清理模型输出并应用技术词替换", OverlayKind.Recognizing));
            text = TextPostProcessor.Process(text, _config.EnablePostProcess);

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowOverlay("未识别到文本", "请靠近麦克风或检查输入设备", OverlayKind.Info, 2200);
                _tray.ShowBalloonTip(2000, "VoiceBridge", "没有识别到文本", ToolTipIcon.Info);
                _logger.Info($"Recognition returned empty text. elapsed={total.Elapsed.TotalSeconds:F2}s");
                return;
            }

            _lastText = text;
            _history.Add(text);
            ((IProgress<RecognitionProgress>)progress).Report(new RecognitionProgress("正在输入", "识别完成，正在粘贴到当前窗口", OverlayKind.Recognizing));
            await InputInjector.PasteTextAsync(text, _config.RestoreClipboard);
            ShowOverlay("已输入", $"{Shorten(text, 80)} · 总耗时 {total.Elapsed.TotalSeconds:F1}s", OverlayKind.Success, 2600);
            _logger.Info($"Recognition completed in {total.Elapsed.TotalSeconds:F2}s: {text}");
        }
        catch (OperationCanceledException ex)
        {
            _logger.Error("Recognition cancelled or timed out", ex);
            ShowOverlay("识别已取消或超时", $"已用时 {total.Elapsed.TotalSeconds:F1}s；可调大超时时间或检查模型服务。", OverlayKind.Error, 3600);
        }
        catch (Exception ex)
        {
            _logger.Error("Recognition failed", ex);
            ShowOverlay("识别失败", FriendlyError(ex.Message), OverlayKind.Error, 4200);
            _tray.ShowBalloonTip(3000, "VoiceBridge", "识别失败：" + FriendlyError(ex.Message), ToolTipIcon.Error);
        }
        finally
        {
            progressTimer.Stop();
            _recognitionCts?.Dispose();
            _recognitionCts = null;
            _recognizing = false;
            _tray.Text = "VoiceBridge 语音桥";
        }
    }

    private async Task PasteLastAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastText))
        {
            ShowOverlay("暂无上次结果", "先完成一次语音识别后再重贴", OverlayKind.Info, 1400);
            return;
        }

        await InputInjector.PasteTextAsync(_lastText, _config.RestoreClipboard);
        ShowOverlay("已重新粘贴", Shorten(_lastText, 80), OverlayKind.Success, 1400);
    }

    private void ShowOverlay(string title, string detail, OverlayKind kind, int autoHideMs = 0)
    {
        if (!_config.ShowOverlay) return;
        _overlay.ShowStatus(title, detail, kind, autoHideMs);
    }

    private static string WithElapsed(string detail, Stopwatch watch) => $"{detail} · 已用时 {watch.Elapsed.TotalSeconds:F0}s";

    private static string FriendlyError(string message)
    {
        if (message.Contains("502", StringComparison.OrdinalIgnoreCase)) return "502 Bad Gateway：服务端网关或模型服务异常，请检查 vLLM/反向代理日志。";
        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase) || message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)) return "连接被拒绝：服务地址不通或端口没有监听。";
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("超时", StringComparison.OrdinalIgnoreCase)) return "请求超时：模型响应慢或超时时间太短。";
        return Shorten(message.Replace("\r", " ").Replace("\n", " "), 160);
    }

    private static string Shorten(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recognitionCts?.Cancel();
            _recognitionCts?.Dispose();
            _keyboard.Dispose();
            _overlay.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AppConfig
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8004";
    public string ModelName { get; set; } = "mimo-v2.5-asr";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
    public Keys HoldKey { get; set; } = Keys.F8;
    public bool AutoStart { get; set; } = false;
    public int MicrophoneDeviceNumber { get; set; } = -1;
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public bool RestoreClipboard { get; set; } = true;
    public bool EnablePostProcess { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public string Prompt { get; set; } = "请把这段音频完整转写成文字，只输出转写结果。语言自动识别。";
    public int MaxTokens { get; set; } = 2048;
}

internal sealed record RecognitionProgress(string Title, string Detail, OverlayKind Kind, int AutoHideMs = 0);

internal static class EndpointUtil
{
    public static string NormalizeBaseEndpoint(string endpoint)
    {
        var value = (endpoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) value = "http://127.0.0.1:8004";
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "http://" + value;
        }

        value = value.TrimEnd('/');
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) value = value[..^3].TrimEnd('/');
        return value;
    }

    public static string ModelsUrl(string endpoint) => NormalizeBaseEndpoint(endpoint) + "/v1/models";

    public static string ChatCompletionsUrl(string endpoint) => NormalizeBaseEndpoint(endpoint) + "/v1/chat/completions";
}

internal static class ConfigStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge");
    private static readonly string PathName = Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(PathName))
        {
            var cfg = new AppConfig();
            Save(cfg);
            return cfg;
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(PathName, Encoding.UTF8)) ?? new AppConfig();
            cfg.Endpoint = EndpointUtil.NormalizeBaseEndpoint(cfg.Endpoint);
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        config.Endpoint = EndpointUtil.NormalizeBaseEndpoint(config.Endpoint);
        File.WriteAllText(PathName, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }
}

internal sealed class SettingsForm : Form
{
    public SettingsForm(AppConfig config)
    {
        Text = "VoiceBridge - 设置";
        Width = 720;
        Height = 660;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Icon = AppIconFactory.CreateIcon();

        var endpoint = TextBox(config.Endpoint);
        endpoint.PlaceholderText = "例如 36.147.35.14:30081 或 http://36.147.35.14:30081";
        var model = TextBox(config.ModelName);
        var apiKey = TextBox(config.ApiKey); apiKey.UseSystemPasswordChar = true;
        var timeout = Numeric(config.TimeoutSeconds, 5, 900);
        var mic = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
        mic.Items.Add(new DeviceItem(-1, "系统默认麦克风"));
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            mic.Items.Add(new DeviceItem(i, caps.ProductName));
        }
        mic.SelectedIndex = Math.Max(0, FindDeviceIndex(mic, config.MicrophoneDeviceNumber));

        var hotkey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var k in new[] { Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12 }) hotkey.Items.Add(k);
        hotkey.SelectedItem = hotkey.Items.Contains(config.HoldKey) ? config.HoldKey : Keys.F8;

        var autostart = new CheckBox { Text = "开机自启", Checked = config.AutoStart, AutoSize = true };
        var restoreClipboard = new CheckBox { Text = "粘贴后恢复原剪贴板", Checked = config.RestoreClipboard, AutoSize = true };
        var postProcess = new CheckBox { Text = "启用技术词后处理", Checked = config.EnablePostProcess, AutoSize = true };
        var showOverlay = new CheckBox { Text = "显示语音状态浮窗", Checked = config.ShowOverlay, AutoSize = true };
        var prompt = new TextBox { Text = config.Prompt, Multiline = true, Width = 460, Height = 84, ScrollBars = ScrollBars.Vertical };
        var maxTokens = Numeric(config.MaxTokens, 64, 8192);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 12 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, "服务地址", endpoint);
        AddRow(table, "模型名称", model);
        AddRow(table, "API Key", apiKey);
        AddRow(table, "超时时间/秒", timeout);
        AddRow(table, "麦克风", mic);
        AddRow(table, "按住说话热键", hotkey);
        AddRow(table, "最大输出 Token", maxTokens);
        AddRow(table, "识别提示词", prompt);
        AddRow(table, "", autostart);
        AddRow(table, "", restoreClipboard);
        AddRow(table, "", postProcess);
        AddRow(table, "", showOverlay);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(16, 4, 16, 4),
            Text = "服务地址可填 IP:端口，保存时自动补 http://；不要填写 /v1/chat/completions。保存后热加载，无需重启。"
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        var ok = new Button { Text = "保存", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
        var test = new Button { Text = "测试连接", Width = 100 };
        test.Click += async (_, _) => await TestEndpointAsync(endpoint.Text, apiKey.Text);
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); buttons.Controls.Add(test);

        ok.Click += (_, _) =>
        {
            config.Endpoint = EndpointUtil.NormalizeBaseEndpoint(endpoint.Text);
            config.ModelName = model.Text.Trim();
            config.ApiKey = apiKey.Text.Trim();
            config.TimeoutSeconds = (int)timeout.Value;
            config.MicrophoneDeviceNumber = ((DeviceItem)mic.SelectedItem!).Number;
            config.HoldKey = (Keys)hotkey.SelectedItem!;
            config.AutoStart = autostart.Checked;
            config.RestoreClipboard = restoreClipboard.Checked;
            config.EnablePostProcess = postProcess.Checked;
            config.ShowOverlay = showOverlay.Checked;
            config.Prompt = prompt.Text.Trim();
            config.MaxTokens = (int)maxTokens.Value;
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(table);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private static TextBox TextBox(string text) => new() { Text = text, Width = 460 };

    private static NumericUpDown Numeric(int value, int min, int max)
    {
        var numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Width = 160
        };
        numeric.Value = Math.Clamp(value, min, max);
        return numeric;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 0, 0) }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static int FindDeviceIndex(ComboBox combo, int deviceNumber)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (((DeviceItem)combo.Items[i]!).Number == deviceNumber) return i;
        }
        return 0;
    }

    private static async Task TestEndpointAsync(string endpoint, string apiKey)
    {
        var sw = Stopwatch.StartNew();
        var normalized = EndpointUtil.NormalizeBaseEndpoint(endpoint);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var url = EndpointUtil.ModelsUrl(normalized);
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            var msg = resp.IsSuccessStatusCode
                ? $"连接成功\n\n地址：{normalized}\n接口：{url}\n耗时：{sw.Elapsed.TotalMilliseconds:F0} ms"
                : $"连接失败：{(int)resp.StatusCode} {resp.ReasonPhrase}\n\n地址：{normalized}\n接口：{url}\n耗时：{sw.Elapsed.TotalMilliseconds:F0} ms\n\n{TrimBody(body)}";
            MessageBox.Show(msg, "VoiceBridge");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败：{ex.Message}\n\n地址：{normalized}\n接口：{EndpointUtil.ModelsUrl(normalized)}", "VoiceBridge");
        }
    }

    private static string TrimBody(string body) => string.IsNullOrWhiteSpace(body) ? string.Empty : (body.Length <= 800 ? body : body[..800] + "…");

    private sealed record DeviceItem(int Number, string Name)
    {
        public override string ToString() => Name;
    }
}

internal sealed class StatusOverlay : Form
{
    private readonly Label _title;
    private readonly Label _detail;
    private readonly System.Windows.Forms.Timer _hideTimer = new();

    public StatusOverlay()
    {
        Width = 500;
        Height = 108;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 36);
        Opacity = 0.94;
        Padding = new Padding(16);
        Icon = AppIconFactory.CreateIcon();

        _title = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            ForeColor = Color.White,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _detail = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.Gainsboro,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9),
            TextAlign = ContentAlignment.MiddleLeft
        };

        Controls.Add(_detail);
        Controls.Add(_title);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowStatus(string title, string detail, OverlayKind kind, int autoHideMs = 0)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowStatus(title, detail, kind, autoHideMs));
            return;
        }

        _title.Text = IconText(kind) + " " + title;
        _detail.Text = detail;
        Location = CalculateLocation();
        Show();
        BringToFront();

        _hideTimer.Stop();
        if (autoHideMs > 0)
        {
            _hideTimer.Interval = autoHideMs;
            _hideTimer.Start();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = RoundedRect(ClientRectangle, 18);
        using var pen = new Pen(Color.FromArgb(80, 255, 255, 255));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    private static Point CalculateLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Left + (area.Width - 500) / 2, area.Bottom - 160);
    }

    private static string IconText(OverlayKind kind) => kind switch
    {
        OverlayKind.Recording => "●",
        OverlayKind.Recognizing => "…",
        OverlayKind.Success => "✓",
        OverlayKind.Error => "!",
        _ => "i"
    };

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

internal enum OverlayKind
{
    Info,
    Recording,
    Recognizing,
    Success,
    Error
}

internal static class AppIconFactory
{
    public static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(33, 132, 255), Color.FromArgb(111, 66, 193), 45f);
        g.FillEllipse(bg, 2, 2, 28, 28);
        using var white = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(white, 16, 8, 16, 18);
        g.DrawArc(white, 10, 10, 12, 12, 0, 180);
        g.DrawLine(white, 16, 22, 16, 25);
        g.DrawLine(white, 11, 25, 21, 25);
        var hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal sealed class AudioRecorder
{
    private readonly int _deviceNumber;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly AppLogger _logger;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _stopped;
    private string _path = string.Empty;

    public AudioRecorder(int deviceNumber, int sampleRate, int channels, AppLogger logger)
    {
        _deviceNumber = deviceNumber;
        _sampleRate = sampleRate;
        _channels = channels;
        _logger = logger;
    }

    public void Start()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge", "recordings");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"rec-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        _stopped = new TaskCompletionSource<string>();
        _waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            WaveFormat = new WaveFormat(_sampleRate, 16, _channels),
            BufferMilliseconds = 50
        };
        _writer = new WaveFileWriter(_path, _waveIn.WaveFormat);
        _waveIn.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _waveIn.RecordingStopped += (_, e) =>
        {
            _writer?.Dispose();
            _writer = null;
            _waveIn?.Dispose();
            _waveIn = null;
            if (e.Exception != null) _stopped?.TrySetException(e.Exception);
            else _stopped?.TrySetResult(_path);
        };
        _waveIn.StartRecording();
    }

    public Task<string> StopAsync()
    {
        _waveIn?.StopRecording();
        return _stopped?.Task ?? Task.FromResult(_path);
    }

    public void Cancel()
    {
        _waveIn?.StopRecording();
        try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception ex) { _logger.Error("Delete cancelled recording failed", ex); }
    }

    public static TimeSpan TryGetDuration(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.TotalTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}

internal sealed class AsrClient
{
    private readonly AppConfig _config;
    private readonly AppLogger _logger;

    public AsrClient(AppConfig config, AppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string wavPath, IProgress<RecognitionProgress>? progress, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var endpoint = EndpointUtil.NormalizeBaseEndpoint(_config.Endpoint);
        var url = EndpointUtil.ChatCompletionsUrl(endpoint);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds) };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        progress?.Report(new RecognitionProgress("正在编码音频", $"读取 wav 并转 base64，超时 {_config.TimeoutSeconds}s", OverlayKind.Recognizing));
        var audioBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
        var data = Convert.ToBase64String(audioBytes);
        _logger.Info($"Audio prepared. file={Path.GetFileName(wavPath)}, size={FileUtil.FormatBytes(audioBytes.Length)}, base64={FileUtil.FormatBytes(data.Length)}, endpoint={endpoint}, model={_config.ModelName}");

        var payload = new
        {
            model = _config.ModelName,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "audio_url", audio_url = new { url = "data:audio/wav;base64," + data } },
                        new { type = "text", text = _config.Prompt }
                    }
                }
            },
            modalities = new[] { "text" },
            temperature = 0,
            max_tokens = _config.MaxTokens
        };

        var json = JsonSerializer.Serialize(payload);
        _logger.Info($"POST {url}; payload={FileUtil.FormatBytes(Encoding.UTF8.GetByteCount(json))}; timeout={_config.TimeoutSeconds}s");
        progress?.Report(new RecognitionProgress("正在请求模型", $"已发送到 {_config.ModelName}，等待 ASR 返回", OverlayKind.Recognizing));

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var resp = await http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        _logger.Info($"ASR response received. status={(int)resp.StatusCode} {resp.ReasonPhrase}; elapsed={total.Elapsed.TotalSeconds:F2}s; body={FileUtil.FormatBytes(Encoding.UTF8.GetByteCount(body))}");

        if (!resp.IsSuccessStatusCode)
        {
            var trimmed = body.Length <= 1200 ? body : body[..1200] + "…";
            _logger.Info("ASR non-success body: " + trimmed);
            throw new InvalidOperationException($"ASR failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{trimmed}");
        }

        progress?.Report(new RecognitionProgress("正在解析结果", "模型已返回，正在提取文本", OverlayKind.Recognizing));
        var text = ExtractText(body);
        _logger.Info($"ASR text extracted. elapsed={total.Elapsed.TotalSeconds:F2}s; chars={text.Length}");
        return text;
    }

    private static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        if (!msg.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text)) sb.Append(text.GetString());
            }
            return sb.ToString();
        }
        return content.ToString();
    }
}

internal static class TextPostProcessor
{
    private static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["威欧拉姆"] = "vLLM",
        ["微欧拉姆"] = "vLLM",
        ["阿森德"] = "Ascend",
        ["纳克斯"] = "Nacos",
        ["卡斯多"] = "Casdoor",
        ["库伯内提斯"] = "Kubernetes",
        ["技能哈勃"] = "SkillHub",
        ["米莫"] = "MiMo",
        ["九一零B"] = "910B",
        ["九幺零B"] = "910B"
    };

    public static string Process(string text, bool enabled)
    {
        text = text.Trim();
        if (!enabled) return text;
        foreach (var kv in Terms) text = text.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("换行", Environment.NewLine)
                   .Replace("逗号", "，")
                   .Replace("句号", "。")
                   .Replace("冒号", "：")
                   .Replace("空格", " ");
        return text.Trim();
    }
}

internal static class InputInjector
{
    public static async Task PasteTextAsync(string text, bool restoreClipboard)
    {
        string? oldText = null;
        try { oldText = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
        Clipboard.SetText(text);
        await Task.Delay(80);
        SendKeys.SendWait("^v");
        await Task.Delay(250);
        if (restoreClipboard && oldText != null) Clipboard.SetText(oldText);
    }

    public static void ReleaseModifiers()
    {
        foreach (var key in new[] { Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.LWin, Keys.RWin })
        {
            keybd_event((byte)key, 0, 0x0002, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

internal sealed class GlobalKeyboardHook : IDisposable
{
    public event Action<Keys>? KeyDown;
    public event Action<Keys>? KeyUp;
    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<Keys> _pressed = new();
    private IntPtr _hook;

    public GlobalKeyboardHook() => _proc = HookCallback;

    public void Start() => _hook = SetHook(_proc);

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(13, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var key = (Keys)vkCode;
            var msg = wParam.ToInt32();
            if (msg == 0x0100 || msg == 0x0104)
            {
                if (_pressed.Add(key)) KeyDown?.Invoke(key);
            }
            if (msg == 0x0101 || msg == 0x0105)
            {
                _pressed.Remove(key);
                KeyUp?.Invoke(key);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static bool IsCtrlAltDown()
    {
        return (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0 && (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "VoiceBridge";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled) key.SetValue(Name, '"' + Application.ExecutablePath + '"');
            else key.DeleteValue(Name, false);
        }
        catch { }
    }
}

internal sealed class HistoryStore
{
    private readonly string _path;

    public HistoryStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "history.txt");
    }

    public void Add(string text)
    {
        File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
    }

    public string ReadAll() => FileUtil.ReadAllTextShared(_path);
}

internal sealed class AppLogger
{
    private readonly object _lock = new();
    public string LogPath { get; }

    public AppLogger()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "voicebridge.log");
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception ex) => Write("ERROR", message + Environment.NewLine + ex);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
    }
}

internal static class FileUtil
{
    public static string ReadAllTextShared(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(path)) return string.Empty;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F1} {units[unit]}";
    }
}

internal sealed class HistoryForm : Form
{
    public HistoryForm(HistoryStore history)
    {
        Text = "VoiceBridge - 识别历史";
        Width = 760;
        Height = 520;
        Icon = AppIconFactory.CreateIcon();
        var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Text = history.ReadAll() };
        Controls.Add(box);
    }
}

internal sealed class LogForm : Form
{
    private readonly string _logPath;
    private readonly TextBox _box;
    private readonly Label _status;
    private readonly CheckBox _autoScroll;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _lastLength = -1;

    public LogForm(string logPath)
    {
        _logPath = logPath;
        Text = "VoiceBridge - 实时日志";
        Width = 980;
        Height = 640;
        Icon = AppIconFactory.CreateIcon();

        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9)
        };

        _status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 3, 8, 3) };
        _autoScroll = new CheckBox { Text = "自动滚动", Checked = true, AutoSize = true };
        var refresh = new Button { Text = "刷新", Width = 80 };
        var copy = new Button { Text = "复制全部", Width = 90 };
        var openDir = new Button { Text = "打开目录", Width = 90 };
        var clear = new Button { Text = "清空日志", Width = 90 };

        refresh.Click += (_, _) => RefreshLog(force: true);
        copy.Click += (_, _) => { if (!string.IsNullOrEmpty(_box.Text)) Clipboard.SetText(_box.Text); };
        openDir.Click += (_, _) => OpenLogInExplorer();
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show("确认清空当前日志？", "VoiceBridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                File.WriteAllText(_logPath, string.Empty, Encoding.UTF8);
                RefreshLog(force: true);
            }
        };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(copy);
        toolbar.Controls.Add(openDir);
        toolbar.Controls.Add(clear);
        toolbar.Controls.Add(_autoScroll);

        Controls.Add(_box);
        Controls.Add(_status);
        Controls.Add(toolbar);

        _timer.Tick += (_, _) => RefreshLog(force: false);
        Load += (_, _) =>
        {
            RefreshLog(force: true);
            _timer.Start();
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void RefreshLog(bool force)
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists)
            {
                _box.Text = string.Empty;
                _status.Text = "日志文件尚未创建。";
                return;
            }

            if (!force && info.Length == _lastLength && info.LastWriteTimeUtc == _lastWriteUtc) return;
            _lastLength = info.Length;
            _lastWriteUtc = info.LastWriteTimeUtc;

            _box.Text = FileUtil.ReadAllTextShared(_logPath);
            if (_autoScroll.Checked)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
            _status.Text = $"实时刷新中 · {FileUtil.FormatBytes(info.Length)} · {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _status.Text = "读取日志失败：" + ex.Message;
        }
    }

    private void OpenLogInExplorer()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开目录失败：" + ex.Message, "VoiceBridge");
        }
    }
}