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
    private bool _recording;
    private bool _recognizing;
    private string _lastText = string.Empty;

    public TrayAppContext()
    {
        _config = ConfigStore.Load();
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
                ConfigStore.Save(_config);
                ApplyRuntimeSettings(showTip: true);
                _logger.Info("Settings saved and applied from UI.");
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
        if (key == _config.HoldKey && !_recording && !_recognizing)
        {
            BeginRecord();
            return;
        }

        if (key == Keys.Escape && _recording)
        {
            CancelRecord();
            return;
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
            ShowOverlay("正在录音", $"{_config.HoldKey} 松开后识别 · {_config.ModelName}", OverlayKind.Recording);
            _logger.Info("Recording started.");
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
        try
        {
            _recording = false;
            _recognizing = true;
            _tray.Text = "VoiceBridge 正在识别...";
            ShowOverlay("正在识别", $"{_config.ModelName} · {_config.Endpoint}", OverlayKind.Recognizing);

            var wav = await (_recorder?.StopAsync() ?? Task.FromResult(string.Empty));
            if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
            {
                ShowOverlay("没有录音文件", "没有可识别的音频", OverlayKind.Info, 1800);
                return;
            }

            var client = new AsrClient(_config, _logger);
            var text = await client.TranscribeAsync(wav);
            text = TextPostProcessor.Process(text, _config.EnablePostProcess);

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowOverlay("未识别到文本", "请靠近麦克风或检查输入设备", OverlayKind.Info, 2200);
                _tray.ShowBalloonTip(2000, "VoiceBridge", "没有识别到文本", ToolTipIcon.Info);
                return;
            }

            _lastText = text;
            _history.Add(text);
            await InputInjector.PasteTextAsync(text, _config.RestoreClipboard);
            ShowOverlay("已输入", Shorten(text, 80), OverlayKind.Success, 2200);
            _logger.Info("Recognition completed: " + text);
        }
        catch (Exception ex)
        {
            _logger.Error("Recognition failed", ex);
            ShowOverlay("识别失败", ex.Message, OverlayKind.Error, 3600);
            _tray.ShowBalloonTip(3000, "VoiceBridge", "识别失败：" + ex.Message, ToolTipIcon.Error);
        }
        finally
        {
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

    private static string Shorten(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(PathName)) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathName, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class SettingsForm : Form
{
    public SettingsForm(AppConfig config)
    {
        Text = "VoiceBridge - 设置";
        Width = 660;
        Height = 590;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Icon = AppIconFactory.CreateIcon();

        var endpoint = TextBox(config.Endpoint);
        var model = TextBox(config.ModelName);
        var apiKey = TextBox(config.ApiKey); apiKey.UseSystemPasswordChar = true;
        var timeout = Numeric(config.TimeoutSeconds, 5, 600);
        var mic = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
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
        var prompt = new TextBox { Text = config.Prompt, Multiline = true, Width = 420, Height = 70, ScrollBars = ScrollBars.Vertical };
        var maxTokens = Numeric(config.MaxTokens, 64, 8192);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 12 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
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
            Height = 34,
            Padding = new Padding(16, 4, 16, 4),
            Text = "保存后立即热加载：热键、模型地址、API Key、超时时间、麦克风等均不需要重启。"
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        var ok = new Button { Text = "保存", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
        var test = new Button { Text = "测试连接", Width = 100 };
        test.Click += async (_, _) => await TestEndpointAsync(endpoint.Text, apiKey.Text);
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); buttons.Controls.Add(test);

        ok.Click += (_, _) =>
        {
            config.Endpoint = endpoint.Text.Trim().TrimEnd('/');
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

        Controls.Add(table);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private static TextBox TextBox(string text) => new() { Text = text, Width = 420 };

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
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var resp = await client.GetAsync(endpoint.TrimEnd('/') + "/v1/models");
            MessageBox.Show(resp.IsSuccessStatusCode ? "连接成功" : $"连接失败：{(int)resp.StatusCode} {resp.ReasonPhrase}", "VoiceBridge");
        }
        catch (Exception ex)
        {
            MessageBox.Show("连接失败：" + ex.Message, "VoiceBridge");
        }
    }

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
        Width = 420;
        Height = 96;
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
            Height = 32,
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
        return new Point(area.Left + (area.Width - 420) / 2, area.Bottom - 150);
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

    public async Task<string> TranscribeAsync(string wavPath)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds) };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(wavPath));
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
        var url = _config.Endpoint.TrimEnd('/') + "/v1/chat/completions";
        _logger.Info("POST " + url);
        var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"ASR failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
        return ExtractText(body);
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
            if (msg == 0x0100 || msg == 0x0104) KeyDown?.Invoke(key);
            if (msg == 0x0101 || msg == 0x0105) KeyUp?.Invoke(key);
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
        File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
    }

    public string ReadAll() => File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
}

internal sealed class AppLogger
{
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
        File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
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
    public LogForm(string logPath)
    {
        Text = "VoiceBridge - 日志";
        Width = 860;
        Height = 560;
        Icon = AppIconFactory.CreateIcon();
        var text = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Text = text };
        Controls.Add(box);
    }
}
