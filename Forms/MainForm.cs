using GrowCastleAuto.Engine;
using GrowCastleAuto.Models;
using GrowCastleAuto.Native;

namespace GrowCastleAuto.Forms;

public sealed class MainForm : Form
{
    private readonly string _appDir;
    private readonly string _configPath;
    private readonly string _templatesDir;
    private readonly string _presetsDir;
    private AppConfig _config;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _isLoadingUi;

    // Header controls
    private ComboBox _cboWindows = null!;
    private Button _btnRefreshWindows = null!;
    private Button _btnPickRegion = null!;
    private Button _btnClearRegion = null!;
    private Button _btnStartStop = null!;
    private Label _lblStatus = null!;
    private Label _lblStats = null!;
    private Label _lblRegionInfo = null!;

    // Left panel: Templates
    private FlowLayoutPanel _templatesPanel = null!;
    private readonly Dictionary<TargetKey, TemplateRowWidgets> _rowWidgets = new();

    // Right panel: Settings & Log
    private NumericUpDown _numStepMin = null!;
    private NumericUpDown _numStepSpan = null!;
    private NumericUpDown _numBlueMinMs = null!;
    private NumericUpDown _numBlueMaxMs = null!;
    private NumericUpDown _numWaitBattleMin = null!;
    private NumericUpDown _numWaitBattleMax = null!;
    private NumericUpDown _numTrackDuration = null!;
    private NumericUpDown _numWaitRepeatMin = null!;
    private NumericUpDown _numWaitRepeatMax = null!;
    private NumericUpDown _numLongPauseLoopsMin = null!;
    private NumericUpDown _numLongPauseLoopsMax = null!;
    private NumericUpDown _numLongPauseDuration = null!;
    private NumericUpDown _numMoveNormalMin = null!;
    private NumericUpDown _numMoveNormalMax = null!;
    private NumericUpDown _numMoveFastMin = null!;
    private NumericUpDown _numMoveFastMax = null!;
    private NumericUpDown _numCurveMin = null!;
    private NumericUpDown _numCurveMax = null!;
    private ComboBox _cboBlueMode = null!;
    private ComboBox _cboBlueOrder = null!;
    private ComboBox _cboPresets = null!;
    private TextBox _txtLog = null!;

    private sealed class TemplateRowWidgets
    {
        public required PictureBox Preview { get; init; }
        public required Label StatusLabel { get; init; }
        public required Label RoiLabel { get; init; }
        public required NumericUpDown ThresholdNum { get; init; }
    }

    public MainForm()
    {
        _appDir = AppContext.BaseDirectory;
        // Prefer workspace/project folder if running via dotnet run, else BaseDirectory
        string cwdConfig = Path.Combine(Environment.CurrentDirectory, "templates");
        string workRoot = Directory.Exists(Path.Combine(Environment.CurrentDirectory, "Models"))
            ? Environment.CurrentDirectory
            : _appDir;

        _configPath = Path.Combine(workRoot, "config.json");
        _templatesDir = Path.Combine(workRoot, "templates");
        _presetsDir = Path.Combine(workRoot, "presets");
        Directory.CreateDirectory(_templatesDir);
        Directory.CreateDirectory(_presetsDir);

        _config = AppConfig.Load(_configPath);

        Text = "Grow Castle AutoClick - Rồng Huyền Thoại (Human Mouse)";
        Size = new Size(1220, 810);
        MinimumSize = new Size(1060, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 22, 31);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 9.5f);

        BuildUi();
        LoadConfigToUi();
        RefreshWindowList();
        RefreshPresetList();
        RefreshAllTemplateRows();
        AppendLog("Sẵn sàng. Nhấn F6 để Bắt đầu/Dừng, F8 để Dừng khẩn cấp.");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.RegisterHotKey(Handle, Win32.HOTKEY_START_STOP, Win32.MOD_NONE, Win32.VK_F6);
        Win32.RegisterHotKey(Handle, Win32.HOTKEY_EMERGENCY_STOP, Win32.MOD_NONE, Win32.VK_F8);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Win32.UnregisterHotKey(Handle, Win32.HOTKEY_START_STOP);
        Win32.UnregisterHotKey(Handle, Win32.HOTKEY_EMERGENCY_STOP);
        StopWorkflow();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == Win32.HOTKEY_START_STOP)
                ToggleStartStop();
            else if (id == Win32.HOTKEY_EMERGENCY_STOP)
                StopWorkflow();
        }
        base.WndProc(ref m);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // === TOP BAR ===
        var topPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(26, 32, 44),
            Padding = new Padding(12, 8, 12, 8)
        };
        root.Controls.Add(topPanel, 0, 0);

        var lblWin = new Label
        {
            Text = "Cửa sổ Game:",
            Location = new Point(12, 14),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 195, 215)
        };
        _cboWindows = new ComboBox
        {
            Location = new Point(112, 10),
            Width = 280,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _cboWindows.SelectedIndexChanged += (_, _) =>
        {
            if (_isLoadingUi) return;
            string sel = _cboWindows.SelectedItem?.ToString() ?? string.Empty;
            _config.TargetWindowTitle = sel.StartsWith("(") ? string.Empty : sel;
            SaveConfig();
            UpdateRegionLabel();
        };

        _btnRefreshWindows = MakeButton("Làm mới", new Point(400, 9), new Size(80, 28), Color.FromArgb(51, 65, 85));
        _btnRefreshWindows.Click += (_, _) => RefreshWindowList();

        _btnPickRegion = MakeButton("Chọn vùng quét", new Point(486, 9), new Size(115, 28), Color.FromArgb(51, 65, 85));
        _btnPickRegion.Click += async (_, _) => await PickCustomRegionAsync();

        _btnClearRegion = MakeButton("Xóa vùng", new Point(607, 9), new Size(78, 28), Color.FromArgb(71, 55, 65));
        _btnClearRegion.Click += (_, _) =>
        {
            _config.CustomCaptureRegion = null;
            SaveConfig();
            UpdateRegionLabel();
        };

        _lblRegionInfo = new Label
        {
            Location = new Point(12, 46),
            Size = new Size(675, 22),
            ForeColor = Color.FromArgb(148, 163, 184),
            Text = "Vùng quét: Toàn màn hình"
        };

        _btnStartStop = MakeButton("▶ BẮT ĐẦU (F6)", new Point(705, 10), new Size(175, 58), Color.FromArgb(22, 163, 74));
        _btnStartStop.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _btnStartStop.Click += (_, _) => ToggleStartStop();

        _lblStatus = new Label
        {
            Location = new Point(895, 12),
            Size = new Size(280, 24),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            Text = "Trạng thái: Đang chờ"
        };

        _lblStats = new Label
        {
            Location = new Point(895, 42),
            Size = new Size(280, 24),
            ForeColor = Color.FromArgb(203, 213, 225),
            Text = "Vòng: 0  |  Nhận: 0  |  Phân rã: 0"
        };

        topPanel.Controls.AddRange([
            lblWin, _cboWindows, _btnRefreshWindows, _btnPickRegion, _btnClearRegion,
            _lblRegionInfo, _btnStartStop, _lblStatus, _lblStats
        ]);

        // === BODY SPLIT ===
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 10, 0, 0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.Controls.Add(body, 0, 1);

        // --- LEFT: Templates ---
        var leftContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 29, 41),
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 8, 0)
        };
        body.Controls.Add(leftContainer, 0, 0);

        var leftHeader = new Panel { Dock = DockStyle.Top, Height = 78 };
        var lblTemplatesTitle = new Label
        {
            Text = "Ảnh mẫu nhận diện (Chụp trực tiếp từ cửa sổ game)",
            Location = new Point(0, 0),
            Size = new Size(420, 40),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 250, 252),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var btnTestAllBlue = MakeButton("🔍 Test tìm tất cả thanh xanh lam", new Point(425, 5), new Size(220, 30), Color.FromArgb(14, 116, 144));
        btnTestAllBlue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnTestAllBlue.Click += async (_, _) => await TestBlueBarsPreviewAsync();
        var btnCheckMini = MakeButton("🧪 Kiểm tra mini-game (không nhấn)", new Point(5, 42), new Size(250, 30), Color.FromArgb(124, 58, 237));
        btnCheckMini.Click += async (_, _) => await TestMiniGameCheckAsync();
        var btnPlayMini = MakeButton("🎮 Chơi thử mini-game 1 lần (nhấn thật)", new Point(262, 42), new Size(270, 30), Color.FromArgb(190, 24, 93));
        btnPlayMini.Click += (_, _) => TestMiniGamePlay();
        leftHeader.Controls.Add(lblTemplatesTitle);
        leftHeader.Controls.Add(btnTestAllBlue);
        leftHeader.Controls.Add(btnCheckMini);
        leftHeader.Controls.Add(btnPlayMini);

        _templatesPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 6, 4, 0)
        };
        leftContainer.Controls.Add(_templatesPanel);
        leftContainer.Controls.Add(leftHeader);

        foreach (TargetKey key in Enum.GetValues<TargetKey>())
        {
            var card = CreateTemplateCard(_config.GetTarget(key));
            _templatesPanel.Controls.Add(card);
        }

        // --- RIGHT: Settings + Log ---
        var rightSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        rightSplit.RowStyles.Add(new RowStyle(SizeType.Absolute, 425));
        rightSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(rightSplit, 1, 0);

        var settingsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 29, 41),
            Padding = new Padding(12),
            AutoScroll = true
        };
        rightSplit.Controls.Add(settingsPanel, 0, 0);
        BuildSettingsControls(settingsPanel);

        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 29, 41),
            Padding = new Padding(10),
            Margin = new Padding(0, 10, 0, 0)
        };
        rightSplit.Controls.Add(logPanel, 0, 1);

        var logHeader = new Panel { Dock = DockStyle.Top, Height = 28 };
        var lblLog = new Label
        {
            Text = "Nhật ký hoạt động",
            Dock = DockStyle.Left,
            Width = 200,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        var btnClearLog = MakeButton("Xóa log", new Point(370, 0), new Size(75, 24), Color.FromArgb(51, 65, 85));
        btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClearLog.Click += (_, _) => _txtLog.Clear();
        logHeader.Controls.Add(lblLog);
        logHeader.Controls.Add(btnClearLog);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(13, 17, 23),
            ForeColor = Color.FromArgb(203, 213, 225),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f)
        };
        logPanel.Controls.Add(_txtLog);
        logPanel.Controls.Add(logHeader);
    }

    private Panel CreateTemplateCard(TargetConfig target)
    {
        var card = new Panel
        {
            Width = 640,
            Height = 82,
            BackColor = Color.FromArgb(30, 37, 52),
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(8)
        };

        var pic = new PictureBox
        {
            Location = new Point(8, 11),
            Size = new Size(92, 60),
            BackColor = Color.FromArgb(15, 19, 28),
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        var lblTitle = new Label
        {
            Location = new Point(108, 7),
            Size = new Size(290, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Text = target.DisplayName
        };

        var lblDesc = new Label
        {
            Location = new Point(108, 28),
            Size = new Size(290, 18),
            ForeColor = Color.FromArgb(148, 163, 184),
            Text = target.Description
        };

        var lblStatus = new Label
        {
            Location = new Point(108, 49),
            Size = new Size(140, 20),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Text = "Chưa có mẫu"
        };

        var lblRoi = new Label
        {
            Location = new Point(250, 49),
            Size = new Size(150, 20),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f),
            Text = "Vùng: Toàn bộ"
        };

        var lblThr = new Label
        {
            Location = new Point(406, 12),
            Size = new Size(56, 18),
            ForeColor = Color.FromArgb(180, 195, 215),
            Text = "Khớp %:"
        };

        var numThr = new NumericUpDown
        {
            Location = new Point(464, 9),
            Width = 56,
            Minimum = 50,
            Maximum = 99,
            Value = (decimal)Math.Round(target.Threshold * 100),
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        numThr.ValueChanged += (_, _) =>
        {
            if (_isLoadingUi) return;
            target.Threshold = (double)numThr.Value / 100.0;
            SaveConfig();
        };

        var btnCapture = MakeButton("📷 Chụp mẫu", new Point(406, 40), new Size(92, 30), Color.FromArgb(37, 99, 235));
        btnCapture.Click += async (_, _) => await CaptureTemplateAsync(target);

        var btnTest = MakeButton("Test", new Point(504, 40), new Size(52, 30), Color.FromArgb(13, 148, 136));
        btnTest.Click += async (_, _) => await TestSingleTemplateAsync(target);

        var btnRoi = MakeButton("Vùng", new Point(560, 40), new Size(46, 30), Color.FromArgb(71, 85, 105));
        btnRoi.Click += async (_, _) => await PickTargetRoiAsync(target);

        var btnClear = MakeButton("✕", new Point(609, 40), new Size(25, 30), Color.FromArgb(127, 29, 29));
        btnClear.Click += (_, _) => ClearTemplateOrRoi(target);

        card.Controls.AddRange([pic, lblTitle, lblDesc, lblStatus, lblRoi, lblThr, numThr, btnCapture, btnTest, btnRoi, btnClear]);

        _rowWidgets[target.Key] = new TemplateRowWidgets
        {
            Preview = pic,
            StatusLabel = lblStatus,
            RoiLabel = lblRoi,
            ThresholdNum = numThr
        };

        return card;
    }

    private void BuildSettingsControls(Panel p)
    {
        int y = 6;

        // Preset Bar
        var lblPreset = new Label
        {
            Text = "Preset:",
            Location = new Point(8, y + 4),
            Size = new Size(52, 22),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 250, 252)
        };
        _cboPresets = new ComboBox
        {
            Location = new Point(62, y),
            Width = 175,
            DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        var btnSavePreset = MakeButton("💾 Lưu", new Point(242, y - 1), new Size(60, 27), Color.FromArgb(37, 99, 235));
        btnSavePreset.Click += (_, _) => SaveCurrentPreset();
        var btnLoadPreset = MakeButton("📂 Tải", new Point(306, y - 1), new Size(58, 27), Color.FromArgb(13, 148, 136));
        btnLoadPreset.Click += (_, _) => LoadSelectedPreset();
        var btnDelPreset = MakeButton("Xóa", new Point(368, y - 1), new Size(50, 27), Color.FromArgb(127, 29, 29));
        btnDelPreset.Click += (_, _) => DeleteSelectedPreset();
        p.Controls.AddRange([lblPreset, _cboPresets, btnSavePreset, btnLoadPreset, btnDelPreset]);
        y += 34;

        var lblHeader = new Label
        {
            Text = "Cài đặt Thời gian & Chuột Giống Người",
            Location = new Point(8, y),
            Size = new Size(420, 24),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248)
        };
        p.Controls.Add(lblHeader);
        y += 28;

        (_numStepMin, _numStepSpan) = AddRangeRow(p, ref y, "Chờ giữa các bước (s): Min + Random*", 0.05m, 5.0m, 2, 0.05m);
        (_numBlueMinMs, _numBlueMaxMs) = AddRangeRow(p, ref y, "Chờ nhấn mỗi thanh xanh (ms):", 10, 500, 0, 5);
        (_numWaitBattleMin, _numWaitBattleMax) = AddRangeRow(p, ref y, "Chờ sau nhấn thanh xanh (~3s):", 0.10m, 30m, 2, 0.1m);

        // Single row for continuous tracking window (2s)
        var lblTrack = new Label
        {
            Text = "Track Trắng & Đỏ liên tục (s):",
            Location = new Point(8, y + 4),
            Size = new Size(238, 22),
            ForeColor = Color.FromArgb(203, 213, 225)
        };
        _numTrackDuration = new NumericUpDown
        {
            Location = new Point(250, y),
            Width = 74,
            Minimum = 0.30m,
            Maximum = 15.0m,
            DecimalPlaces = 2,
            Increment = 0.20m,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        _numTrackDuration.ValueChanged += (_, _) => SaveUiSettings();
        p.Controls.AddRange([lblTrack, _numTrackDuration]);
        y += 30;

        (_numWaitRepeatMin, _numWaitRepeatMax) = AddRangeRow(p, ref y, "Chờ lặp lại từ bước 1 (~0.1s):", 0.01m, 60m, 2, 0.05m);
        (_numLongPauseLoopsMin, _numLongPauseLoopsMax) = AddRangeRow(p, ref y, "Nghỉ định kỳ sau mỗi (vòng):", 1, 200, 0, 1);

        var lblLongPauseDuration = new Label
        {
            Text = "Thời gian nghỉ định kỳ (s):",
            Location = new Point(8, y + 4),
            Size = new Size(238, 22),
            ForeColor = Color.FromArgb(203, 213, 225)
        };
        _numLongPauseDuration = new NumericUpDown
        {
            Location = new Point(250, y),
            Width = 74,
            Minimum = 0.1m,
            Maximum = 60m,
            DecimalPlaces = 1,
            Increment = 0.5m,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        _numLongPauseDuration.ValueChanged += (_, _) => SaveUiSettings();
        p.Controls.AddRange([lblLongPauseDuration, _numLongPauseDuration]);
        y += 30;

        (_numMoveNormalMin, _numMoveNormalMax) = AddRangeRow(p, ref y, "Di chuột thường (ms):", 40, 1500, 0, 10);
        (_numMoveFastMin, _numMoveFastMax) = AddRangeRow(p, ref y, "Di chuột nhanh thanh xanh (ms):", 10, 300, 0, 5);
        (_numCurveMin, _numCurveMax) = AddRangeRow(p, ref y, "Độ cong quỹ đạo Bézier:", 0.02m, 0.55m, 2, 0.02m);

        // Blue bar mode & order
        var lblMode = new Label { Text = "Tìm thanh xanh bằng:", Location = new Point(8, y + 4), Size = new Size(165, 22) };
        _cboBlueMode = new ComboBox
        {
            Location = new Point(178, y),
            Width = 240,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        _cboBlueMode.Items.AddRange(["Mẫu ảnh (Template Matching)", "Màu xanh lam (Color Blob)"]);
        _cboBlueMode.SelectedIndexChanged += (_, _) => SaveUiSettings();
        p.Controls.AddRange([lblMode, _cboBlueMode]);
        y += 30;

        var lblOrder = new Label { Text = "Thứ tự nhấn thanh xanh:", Location = new Point(8, y + 4), Size = new Size(165, 22) };
        _cboBlueOrder = new ComboBox
        {
            Location = new Point(178, y),
            Width = 240,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        _cboBlueOrder.Items.AddRange(["Tự nhiên ngẫu nhiên (Khuyên dùng)", "Từ trái sang phải", "Từ trên xuống dưới", "Xáo trộn hoàn toàn"]);
        _cboBlueOrder.SelectedIndexChanged += (_, _) => SaveUiSettings();
        p.Controls.AddRange([lblOrder, _cboBlueOrder]);
        y += 32;

        var btnTestMouse = MakeButton("🖱️ Test di chuột đường cong ngẫu nhiên", new Point(8, y), new Size(275, 30), Color.FromArgb(99, 102, 241));
        btnTestMouse.Click += async (_, _) => await TestHumanMouseAsync();
        p.Controls.Add(btnTestMouse);

        var btnResetDefaults = MakeButton("Mặc định", new Point(290, y), new Size(128, 30), Color.FromArgb(71, 85, 105));
        btnResetDefaults.Click += (_, _) => ResetTimingsToDefault();
        p.Controls.Add(btnResetDefaults);
    }

    private (NumericUpDown, NumericUpDown) AddRangeRow(
        Panel p, ref int y, string label, decimal min, decimal max, int decimals, decimal increment)
    {
        var lbl = new Label
        {
            Text = label,
            Location = new Point(8, y + 4),
            Size = new Size(238, 22),
            ForeColor = Color.FromArgb(203, 213, 225)
        };
        var n1 = new NumericUpDown
        {
            Location = new Point(250, y),
            Width = 74,
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = increment,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };
        var lblDash = new Label
        {
            Text = "–",
            Location = new Point(328, y + 4),
            Size = new Size(14, 20),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var n2 = new NumericUpDown
        {
            Location = new Point(344, y),
            Width = 74,
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = increment,
            BackColor = Color.FromArgb(35, 43, 59),
            ForeColor = Color.White
        };

        n1.ValueChanged += (_, _) => SaveUiSettings();
        n2.ValueChanged += (_, _) => SaveUiSettings();

        p.Controls.AddRange([lbl, n1, lblDash, n2]);
        y += 30;
        return (n1, n2);
    }

    private void LoadConfigToUi()
    {
        _isLoadingUi = true;
        try
        {
            _numStepMin.Value = ClampDec((decimal)_config.StepDelayMinSeconds, _numStepMin);
            _numStepSpan.Value = ClampDec((decimal)_config.StepDelayRandomSpanSeconds, _numStepSpan);
            _numBlueMinMs.Value = ClampDec(_config.BlueBarDelayMinMs, _numBlueMinMs);
            _numBlueMaxMs.Value = ClampDec(_config.BlueBarDelayMaxMs, _numBlueMaxMs);
            _numWaitBattleMin.Value = ClampDec((decimal)_config.WaitAfterBlueBarsMinSeconds, _numWaitBattleMin);
            _numWaitBattleMax.Value = ClampDec((decimal)_config.WaitAfterBlueBarsMaxSeconds, _numWaitBattleMax);
            _numTrackDuration.Value = ClampDec((decimal)_config.LootTrackDurationSeconds, _numTrackDuration);
            _numWaitRepeatMin.Value = ClampDec((decimal)_config.WaitBeforeRepeatMinSeconds, _numWaitRepeatMin);
            _numWaitRepeatMax.Value = ClampDec((decimal)_config.WaitBeforeRepeatMaxSeconds, _numWaitRepeatMax);
            _numLongPauseLoopsMin.Value = ClampDec(_config.LongPauseEveryMinLoops, _numLongPauseLoopsMin);
            _numLongPauseLoopsMax.Value = ClampDec(_config.LongPauseEveryMaxLoops, _numLongPauseLoopsMax);
            _numLongPauseDuration.Value = ClampDec((decimal)_config.LongPauseDurationSeconds, _numLongPauseDuration);
            _numMoveNormalMin.Value = ClampDec(_config.NormalMoveDurationMinMs, _numMoveNormalMin);
            _numMoveNormalMax.Value = ClampDec(_config.NormalMoveDurationMaxMs, _numMoveNormalMax);
            _numMoveFastMin.Value = ClampDec(_config.FastMoveDurationMinMs, _numMoveFastMin);
            _numMoveFastMax.Value = ClampDec(_config.FastMoveDurationMaxMs, _numMoveFastMax);
            _numCurveMin.Value = ClampDec((decimal)_config.CurveIntensityMin, _numCurveMin);
            _numCurveMax.Value = ClampDec((decimal)_config.CurveIntensityMax, _numCurveMax);
            _cboBlueMode.SelectedIndex = (int)_config.BlueBarMode;
            _cboBlueOrder.SelectedIndex = (int)_config.BlueBarOrder;

            foreach (TargetKey key in Enum.GetValues<TargetKey>())
            {
                if (_rowWidgets.TryGetValue(key, out var widget))
                {
                    var t = _config.GetTarget(key);
                    widget.ThresholdNum.Value = ClampDec((decimal)Math.Round(t.Threshold * 100), widget.ThresholdNum);
                }
            }

            UpdateRegionLabel();
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void SaveUiSettings()
    {
        if (_isLoadingUi) return;

        _config.StepDelayMinSeconds = (double)_numStepMin.Value;
        _config.StepDelayRandomSpanSeconds = (double)_numStepSpan.Value;
        _config.BlueBarDelayMinMs = (int)_numBlueMinMs.Value;
        _config.BlueBarDelayMaxMs = Math.Max(_config.BlueBarDelayMinMs, (int)_numBlueMaxMs.Value);
        _config.WaitAfterBlueBarsMinSeconds = (double)_numWaitBattleMin.Value;
        _config.WaitAfterBlueBarsMaxSeconds = Math.Max(_config.WaitAfterBlueBarsMinSeconds, (double)_numWaitBattleMax.Value);
        _config.LootTrackDurationSeconds = (double)_numTrackDuration.Value;
        _config.WaitBeforeRepeatMinSeconds = (double)_numWaitRepeatMin.Value;
        _config.WaitBeforeRepeatMaxSeconds = Math.Max(_config.WaitBeforeRepeatMinSeconds, (double)_numWaitRepeatMax.Value);
        _config.LongPauseEveryMinLoops = (int)_numLongPauseLoopsMin.Value;
        _config.LongPauseEveryMaxLoops = Math.Max(_config.LongPauseEveryMinLoops, (int)_numLongPauseLoopsMax.Value);
        _config.LongPauseDurationSeconds = (double)_numLongPauseDuration.Value;
        _config.NormalMoveDurationMinMs = (int)_numMoveNormalMin.Value;
        _config.NormalMoveDurationMaxMs = Math.Max(_config.NormalMoveDurationMinMs, (int)_numMoveNormalMax.Value);
        _config.FastMoveDurationMinMs = (int)_numMoveFastMin.Value;
        _config.FastMoveDurationMaxMs = Math.Max(_config.FastMoveDurationMinMs, (int)_numMoveFastMax.Value);
        _config.CurveIntensityMin = (double)_numCurveMin.Value;
        _config.CurveIntensityMax = Math.Max(_config.CurveIntensityMin, (double)_numCurveMax.Value);
        _config.BlueBarMode = (BlueBarDetectMode)Math.Clamp(_cboBlueMode.SelectedIndex, 0, 1);
        _config.BlueBarOrder = (BlueBarClickOrder)Math.Clamp(_cboBlueOrder.SelectedIndex, 0, 3);

        SaveConfig();
    }

    private void ResetTimingsToDefault()
    {
        var def = new AppConfig();
        _config.StepDelayMinSeconds = def.StepDelayMinSeconds;
        _config.StepDelayRandomSpanSeconds = def.StepDelayRandomSpanSeconds;
        _config.BlueBarDelayMinMs = def.BlueBarDelayMinMs;
        _config.BlueBarDelayMaxMs = def.BlueBarDelayMaxMs;
        _config.WaitAfterBlueBarsMinSeconds = def.WaitAfterBlueBarsMinSeconds;
        _config.WaitAfterBlueBarsMaxSeconds = def.WaitAfterBlueBarsMaxSeconds;
        _config.LootTrackDurationSeconds = def.LootTrackDurationSeconds;
        _config.WaitBeforeRepeatMinSeconds = def.WaitBeforeRepeatMinSeconds;
        _config.WaitBeforeRepeatMaxSeconds = def.WaitBeforeRepeatMaxSeconds;
        _config.LongPauseEveryMinLoops = def.LongPauseEveryMinLoops;
        _config.LongPauseEveryMaxLoops = def.LongPauseEveryMaxLoops;
        _config.LongPauseDurationSeconds = def.LongPauseDurationSeconds;
        _config.NormalMoveDurationMinMs = def.NormalMoveDurationMinMs;
        _config.NormalMoveDurationMaxMs = def.NormalMoveDurationMaxMs;
        _config.FastMoveDurationMinMs = def.FastMoveDurationMinMs;
        _config.FastMoveDurationMaxMs = def.FastMoveDurationMaxMs;
        _config.CurveIntensityMin = def.CurveIntensityMin;
        _config.CurveIntensityMax = def.CurveIntensityMax;
        LoadConfigToUi();
        SaveConfig();
        AppendLog("Đã khôi phục thông số thời gian & chuột về mặc định.");
    }

    private void RefreshPresetList(string? selectName = null)
    {
        string currentText = selectName ?? _cboPresets.Text;
        _cboPresets.Items.Clear();

        if (Directory.Exists(_presetsDir))
        {
            foreach (var dir in Directory.GetDirectories(_presetsDir).OrderBy(d => Path.GetFileName(d)))
            {
                _cboPresets.Items.Add(Path.GetFileName(dir));
            }
        }

        if (!string.IsNullOrWhiteSpace(currentText) && _cboPresets.Items.Contains(currentText))
            _cboPresets.SelectedItem = currentText;
        else if (_cboPresets.Items.Count > 0)
            _cboPresets.SelectedIndex = 0;
        else
            _cboPresets.Text = "RongHuyenThoai";
    }

    private void SaveCurrentPreset()
    {
        string name = _cboPresets.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "Tên preset không hợp lệ.", "Preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string presetDir = Path.Combine(_presetsDir, name);
        string presetTplDir = Path.Combine(presetDir, "templates");
        Directory.CreateDirectory(presetTplDir);

        _config.Save(Path.Combine(presetDir, "config.json"));
        foreach (var file in Directory.GetFiles(_templatesDir, "*.png"))
        {
            File.Copy(file, Path.Combine(presetTplDir, Path.GetFileName(file)), overwrite: true);
        }

        RefreshPresetList(name);
        AppendLog($"Đã lưu preset '{name}' (cấu hình + ảnh mẫu).");
    }

    private void LoadSelectedPreset()
    {
        string name = _cboPresets.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        string presetDir = Path.Combine(_presetsDir, name);
        if (!Directory.Exists(presetDir))
        {
            MessageBox.Show(this, $"Không tìm thấy preset '{name}'.", "Preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string presetCfgPath = Path.Combine(presetDir, "config.json");
        if (File.Exists(presetCfgPath))
        {
            _config = AppConfig.Load(presetCfgPath);
            SaveConfig();
        }

        string presetTplDir = Path.Combine(presetDir, "templates");
        if (Directory.Exists(presetTplDir))
        {
            foreach (var existing in Directory.GetFiles(_templatesDir, "*.png"))
                File.Delete(existing);

            foreach (var file in Directory.GetFiles(presetTplDir, "*.png"))
                File.Copy(file, Path.Combine(_templatesDir, Path.GetFileName(file)), overwrite: true);
        }

        LoadConfigToUi();
        RefreshWindowList();
        RefreshAllTemplateRows();
        AppendLog($"Đã tải preset '{name}'.");
    }

    private void DeleteSelectedPreset()
    {
        string name = _cboPresets.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        string presetDir = Path.Combine(_presetsDir, name);
        if (!Directory.Exists(presetDir))
            return;

        if (MessageBox.Show(this, $"Xóa preset '{name}'?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        Directory.Delete(presetDir, recursive: true);
        RefreshPresetList();
        AppendLog($"Đã xóa preset '{name}'.");
    }

    private void RefreshWindowList()
    {
        _isLoadingUi = true;
        try
        {
            _cboWindows.Items.Clear();
            _cboWindows.Items.Add("(Toàn màn hình / Vùng tùy chỉnh)");

            var wins = Win32.GetVisibleWindows()
                .Where(w => !string.Equals(w.Title, Text, StringComparison.Ordinal))
                .OrderBy(w => w.Title)
                .ToList();

            int selectIdx = 0;
            foreach (var w in wins)
            {
                int idx = _cboWindows.Items.Add(w.Title);
                if (!string.IsNullOrWhiteSpace(_config.TargetWindowTitle) &&
                    string.Equals(w.Title, _config.TargetWindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    selectIdx = idx;
                }
            }

            _cboWindows.SelectedIndex = selectIdx;
        }
        finally
        {
            _isLoadingUi = false;
        }
        UpdateRegionLabel();
    }

    private void UpdateRegionLabel()
    {
        var bounds = ScreenMatcher.ResolveCaptureBounds(_config);
        if (!string.IsNullOrWhiteSpace(_config.TargetWindowTitle))
            _lblRegionInfo.Text = $"Vùng quét: Cửa sổ '{_config.TargetWindowTitle}' ({bounds.Width}×{bounds.Height} tại {bounds.X},{bounds.Y})";
        else if (_config.CustomCaptureRegion.HasValue)
            _lblRegionInfo.Text = $"Vùng quét tùy chỉnh: {bounds.Width}×{bounds.Height} tại ({bounds.X}, {bounds.Y})";
        else
            _lblRegionInfo.Text = $"Vùng quét: Toàn màn hình ({bounds.Width}×{bounds.Height})";
    }

    private void RefreshAllTemplateRows()
    {
        foreach (TargetKey key in Enum.GetValues<TargetKey>())
        {
            var cfg = _config.GetTarget(key);
            var w = _rowWidgets[key];
            string path = Path.Combine(_templatesDir, cfg.TemplateFileName);

            w.Preview.Image?.Dispose();
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var tmp = new Bitmap(fs);
                w.Preview.Image = new Bitmap(tmp);
                w.StatusLabel.Text = $"✓ Đã có ({tmp.Width}×{tmp.Height})";
                w.StatusLabel.ForeColor = Color.FromArgb(74, 222, 128);
            }
            else
            {
                w.Preview.Image = null;
                w.StatusLabel.Text = "✗ Chưa có mẫu";
                w.StatusLabel.ForeColor = Color.FromArgb(248, 113, 113);
            }

            w.RoiLabel.Text = cfg.SearchRoi != null ? "Vùng tìm: Đã giới hạn" : "Vùng tìm: Toàn bộ";
        }
    }

    private async Task CaptureTemplateAsync(TargetConfig target)
    {
        Visible = false;
        await Task.Delay(220);
        try
        {
            using var snip = new SnipOverlayForm($"Chụp mẫu: {target.DisplayName}");
            if (snip.ShowDialog() == DialogResult.OK && snip.CapturedBitmap != null)
            {
                string path = Path.Combine(_templatesDir, target.TemplateFileName);
                snip.CapturedBitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                snip.CapturedBitmap.Dispose();
                AppendLog($"Đã lưu ảnh mẫu '{target.DisplayName}' -> {target.TemplateFileName}");
                RefreshAllTemplateRows();
            }
        }
        finally
        {
            Visible = true;
            Activate();
        }
    }

    private async Task PickCustomRegionAsync()
    {
        Visible = false;
        await Task.Delay(220);
        try
        {
            using var snip = new SnipOverlayForm("Kéo chọn toàn bộ khung Game");
            if (snip.ShowDialog() == DialogResult.OK)
            {
                snip.CapturedBitmap?.Dispose();
                _config.CustomCaptureRegion = snip.SelectedScreenRect;
                _config.TargetWindowTitle = string.Empty;
                _isLoadingUi = true;
                _cboWindows.SelectedIndex = 0;
                _isLoadingUi = false;
                SaveConfig();
                UpdateRegionLabel();
                AppendLog($"Đã đặt vùng quét tùy chỉnh: {snip.SelectedScreenRect}");
            }
        }
        finally
        {
            Visible = true;
            Activate();
        }
    }

    private async Task PickTargetRoiAsync(TargetConfig target)
    {
        var captureBounds = ScreenMatcher.ResolveCaptureBounds(_config);
        Visible = false;
        await Task.Delay(220);
        try
        {
            using var snip = new SnipOverlayForm($"Chọn vùng tìm kiếm giới hạn cho: {target.DisplayName}");
            if (snip.ShowDialog() == DialogResult.OK)
            {
                snip.CapturedBitmap?.Dispose();
                var sel = Rectangle.Intersect(captureBounds, snip.SelectedScreenRect);
                if (sel.Width >= 10 && sel.Height >= 10)
                {
                    target.SearchRoi = new NormalizedRect
                    {
                        X = (double)(sel.X - captureBounds.X) / captureBounds.Width,
                        Y = (double)(sel.Y - captureBounds.Y) / captureBounds.Height,
                        Width = (double)sel.Width / captureBounds.Width,
                        Height = (double)sel.Height / captureBounds.Height
                    };
                    SaveConfig();
                    RefreshAllTemplateRows();
                    AppendLog($"Đã đặt vùng tìm kiếm cho '{target.DisplayName}'.");
                }
            }
        }
        finally
        {
            Visible = true;
            Activate();
        }
    }

    private void ClearTemplateOrRoi(TargetConfig target)
    {
        if (target.SearchRoi != null)
        {
            target.SearchRoi = null;
            SaveConfig();
            RefreshAllTemplateRows();
            AppendLog($"Đã xóa vùng giới hạn của '{target.DisplayName}'.");
            return;
        }

        string path = Path.Combine(_templatesDir, target.TemplateFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            RefreshAllTemplateRows();
            AppendLog($"Đã xóa ảnh mẫu '{target.DisplayName}'.");
        }
    }

    /// <summary>
    /// Captures the game area for a Test button. This tool's own window is usually in front of (or
    /// overlapping) the game, and a screen capture would then contain this tool instead of the game,
    /// so the game is brought to the front first.
    /// </summary>
    private async Task<(Bitmap Screen, Rectangle Bounds)> CaptureGameForTestAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.TargetWindowTitle))
        {
            Win32.BringWindowToFrontByTitle(_config.TargetWindowTitle);
            await Task.Delay(350);
        }

        var bounds = ScreenMatcher.ResolveCaptureBounds(_config);
        return (ScreenMatcher.CaptureScreen(bounds), bounds);
    }

    private async Task TestSingleTemplateAsync(TargetConfig target)
    {
        string path = Path.Combine(_templatesDir, target.TemplateFileName);
        if (!File.Exists(path))
        {
            MessageBox.Show($"Chưa có ảnh mẫu cho '{target.DisplayName}'. Hãy bấm 'Chụp mẫu' trước.", "Thiếu mẫu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (screenBmp, bounds) = await CaptureGameForTestAsync();
        using var screen = screenBmp;
        using var tpl = new Bitmap(path);

        var matches = target.Key == TargetKey.Step4BlueBar
            ? ScreenMatcher.FindAllMatches(screen, bounds.Location, tpl, target)
            : (ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, target) is { } m ? [m] : new List<MatchResult>());

        if (matches.Count == 0 && target.Key != TargetKey.Step4BlueBar)
        {
            var nearest = ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, target, thresholdOverride: 0.40);
            AppendLog(nearest != null
                ? $"[Test '{target.DisplayName}'] Không đạt ngưỡng: giống nhất {nearest.Score:P1} (cần ≥ {target.Threshold:P0}) tại ({nearest.ScreenBounds.X},{nearest.ScreenBounds.Y}). Thử hạ ngưỡng hoặc chụp lại mẫu."
                : $"[Test '{target.DisplayName}'] Không có vùng nào giống quá 40%. Màn hình game lúc này có đang ở đúng bước không?");
        }

        var annotated = new Bitmap(screen);
        using (var g = Graphics.FromImage(annotated))
        {
            using var pen = new Pen(Color.Lime, 2.5f);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
            foreach (var hit in matches)
            {
                var rel = new Rectangle(hit.ScreenBounds.X - bounds.X, hit.ScreenBounds.Y - bounds.Y, hit.ScreenBounds.Width, hit.ScreenBounds.Height);
                g.DrawRectangle(pen, rel);
                g.DrawString($"{hit.Score:P1}", font, Brushes.Yellow, rel.X, Math.Max(0, rel.Y - 20));
            }
        }

        AppendLog($"[Test '{target.DisplayName}'] Tìm thấy {matches.Count} vị trí khớp.");
        using var preview = new PreviewForm($"Kết quả Test: {target.DisplayName} ({matches.Count} khớp)", annotated);
        preview.ShowDialog(this);
    }

    private async Task TestBlueBarsPreviewAsync()
    {
        var cfg = _config.GetTarget(TargetKey.Step4BlueBar);
        var (screenBmp, bounds) = await CaptureGameForTestAsync();
        using var screen = screenBmp;

        List<MatchResult> matches;
        if (_config.BlueBarMode == BlueBarDetectMode.BlueColorBlob)
        {
            matches = ScreenMatcher.FindBlueBarBlobs(screen, bounds.Location, cfg.SearchRoi);
        }
        else
        {
            string path = Path.Combine(_templatesDir, cfg.TemplateFileName);
            if (!File.Exists(path))
            {
                MessageBox.Show("Chưa có ảnh mẫu thanh màu xanh lam (hoặc chuyển sang chế độ 'Màu xanh lam').", "Thiếu mẫu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using var tpl = new Bitmap(path);
            matches = ScreenMatcher.FindAllMatches(screen, bounds.Location, tpl, cfg);
        }

        var annotated = new Bitmap(screen);
        using (var g = Graphics.FromImage(annotated))
        {
            using var pen = new Pen(Color.Cyan, 2.5f);
            using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            for (int i = 0; i < matches.Count; i++)
            {
                var hit = matches[i];
                var rel = new Rectangle(hit.ScreenBounds.X - bounds.X, hit.ScreenBounds.Y - bounds.Y, hit.ScreenBounds.Width, hit.ScreenBounds.Height);
                g.DrawRectangle(pen, rel);
                g.DrawString($"#{i + 1} ({hit.Score:P0})", font, Brushes.Yellow, rel.X, Math.Max(0, rel.Y - 18));
            }
        }

        AppendLog($"[Test thanh xanh lam] Tìm thấy {matches.Count} thanh.");
        using var preview = new PreviewForm($"Test tìm thanh xanh lam: {matches.Count} thanh", annotated);
        preview.ShowDialog(this);
    }

    private WorkflowRunner CreateTestRunner() => new(
        _templatesDir,
        msg => SafeUi(() => AppendLog(msg)),
        stats => SafeUi(() => _lblStatus.Text = $"Trạng thái: {stats.CurrentStep}"));

    private bool IsRunning() => _runTask != null && !_runTask.IsCompleted;

    /// <summary>Test 1: mini-game detection only. Nothing is clicked.</summary>
    private async Task TestMiniGameCheckAsync()
    {
        if (IsRunning())
        {
            MessageBox.Show(this, "Hãy dừng quy trình đang chạy trước khi test.", "Đang chạy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppendLog("[Test mini-game] Kiểm tra nhận diện (không nhấn gì). Mở mini-game 'Kim cương ở đâu?' (chưa bấm START)...");
        if (!string.IsNullOrWhiteSpace(_config.TargetWindowTitle))
        {
            Win32.BringWindowToFrontByTitle(_config.TargetWindowTitle);
            await Task.Delay(500);
        }

        WorkflowRunner.MiniGameTestReport report;
        try
        {
            var runner = CreateTestRunner();
            report = await Task.Run(() => runner.InspectMiniGame(_config, CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppendLog($"[Test mini-game] LỖI: {ex.Message}");
            return;
        }

        foreach (string line in report.Lines)
            AppendLog($"[Test mini-game] {line}");

        if (report.Image != null)
        {
            using var preview = new PreviewForm(
                report.Ok ? "Test mini-game: ĐẠT" : "Test mini-game: CHƯA ĐẠT", report.Image);
            preview.ShowDialog(this);
        }
    }

    /// <summary>Test 2: plays the mini-game once for real. Uses the normal stop button / F6 / F8.</summary>
    private void TestMiniGamePlay()
    {
        if (IsRunning())
        {
            MessageBox.Show(this, "Hãy dừng quy trình đang chạy trước khi test.", "Đang chạy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Sẽ đưa game lên trước, chờ tối đa 20 giây cho nút START của mini-game xuất hiện, rồi chơi MỘT lần " +
            "(di chuột và nhấn thật).\n\nF8 hoặc nút DỪNG để dừng khẩn cấp. Tiếp tục?",
            "Chơi thử mini-game", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnStartStop.Text = "⏹ DỪNG (F6 / F8)";
        _btnStartStop.BackColor = Color.FromArgb(220, 38, 38);

        var runner = CreateTestRunner();
        string title = _config.TargetWindowTitle;

        _runTask = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Win32.BringWindowToFrontByTitle(title);
                    await Task.Delay(500, ct);
                }

                await runner.TestPlayMiniGameAsync(_config, 20, ct);
            }
            catch (OperationCanceledException)
            {
                SafeUi(() => AppendLog("[Test mini-game] Đã dừng theo yêu cầu."));
            }
            catch (Exception ex)
            {
                SafeUi(() => AppendLog($"[Test mini-game] LỖI: {ex.Message}"));
            }
            finally
            {
                try { Win32.LeftUp(); } catch { }

                SafeUi(() =>
                {
                    _btnStartStop.Text = "▶ BẮT ĐẦU (F6)";
                    _btnStartStop.BackColor = Color.FromArgb(22, 163, 74);
                    _lblStatus.Text = "Trạng thái: Đã dừng";
                });
            }
        });
    }

    private async Task TestHumanMouseAsync()
    {
        AppendLog("Đang test di chuyển chuột theo đường cong ngẫu nhiên...");
        var bounds = ScreenMatcher.ResolveCaptureBounds(_config);
        int cx = bounds.Left + bounds.Width / 3 + Random.Shared.Next(bounds.Width / 3);
        int cy = bounds.Top + bounds.Height / 3 + Random.Shared.Next(bounds.Height / 3);
        using var cts = new CancellationTokenSource(5000);
        await HumanMouse.MoveToAsync(new Point(cx, cy), _config, fastMode: false, cts.Token);
        AppendLog($"Đã di chuyển tới ({cx}, {cy}) bằng đường cong Bézier ngẫu nhiên.");
    }

    private void ToggleStartStop()
    {
        if (_runTask != null && !_runTask.IsCompleted)
            StopWorkflow();
        else
            StartWorkflow();
    }

    private void StartWorkflow()
    {
        if (_runTask != null && !_runTask.IsCompleted)
            return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnStartStop.Text = "⏹ DỪNG (F6 / F8)";
        _btnStartStop.BackColor = Color.FromArgb(220, 38, 38);

        var runner = new WorkflowRunner(
            _templatesDir,
            msg => SafeUi(() => AppendLog(msg)),
            stats => SafeUi(() =>
            {
                _lblStatus.Text = $"Trạng thái: {stats.CurrentStep}";
                _lblStats.Text = $"Vòng: {stats.Loops}  |  Nhận: {stats.Claimed}  |  Phân rã: {stats.Dismantled}";
            }));

        _runTask = Task.Run(async () =>
        {
            try
            {
                await runner.RunLoopAsync(_config, ct);
            }
            catch (OperationCanceledException)
            {
                SafeUi(() => AppendLog("Đã dừng theo yêu cầu người dùng."));
            }
            catch (Exception ex)
            {
                SafeUi(() =>
                {
                    AppendLog($"[LỖI] {ex.Message}");
                    MessageBox.Show(this, ex.Message, "Lỗi quy trình", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            finally
            {
                // Make sure no synthetic mouse button stays pressed after a stop
                try { Win32.LeftUp(); } catch { }

                SafeUi(() =>
                {
                    _btnStartStop.Text = "▶ BẮT ĐẦU (F6)";
                    _btnStartStop.BackColor = Color.FromArgb(22, 163, 74);
                    _lblStatus.Text = "Trạng thái: Đã dừng";
                });
            }
        });
    }

    private void StopWorkflow()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            AppendLog("Đang dừng quy trình...");

            if (!IsDisposed)
            {
                _btnStartStop.Text = "⏳ Đang dừng...";
                _btnStartStop.BackColor = Color.FromArgb(217, 119, 6);
                _ = WarnIfStopIsSlowAsync();
            }
        }
    }

    private async Task WarnIfStopIsSlowAsync()
    {
        var task = _runTask;
        await Task.Delay(3000);
        if (!IsDisposed && task != null && !task.IsCompleted)
            AppendLog("[Cảnh báo] Quy trình vẫn chưa dừng sau 3s. Vui lòng chờ thêm hoặc đóng ứng dụng.");
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

        // Keep the log small: a huge TextBox makes the UI (and the stop button) sluggish
        if (_txtLog.TextLength > 30000)
        {
            string text = _txtLog.Text;
            int cut = text.IndexOf('\n', text.Length - 15000);
            _txtLog.Text = cut >= 0 ? text.Substring(cut + 1) : string.Empty;
        }

        _txtLog.AppendText(line);
    }

    private void SaveConfig() => _config.Save(_configPath);

    private static decimal ClampDec(decimal val, NumericUpDown num) =>
        Math.Clamp(val, num.Minimum, num.Maximum);

    private static Button MakeButton(string text, Point loc, Size size, Color bg) => new()
    {
        Text = text,
        Location = loc,
        Size = size,
        BackColor = bg,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand
    };
}
