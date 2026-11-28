using GrowCastleAuto.Models;
using GrowCastleAuto.Native;

namespace GrowCastleAuto.Engine;

public sealed class RunStats
{
    public int Loops { get; set; }
    public int Claimed { get; set; }
    public int Dismantled { get; set; }
    public string CurrentStep { get; set; } = "Đang chờ";
}

public sealed class WorkflowRunner
{
    private readonly string _templatesDir;
    private readonly Action<string> _log;
    private readonly Action<RunStats> _onStatsChanged;
    private readonly RunStats _stats = new();

    public WorkflowRunner(string templatesDir, Action<string> log, Action<RunStats> onStatsChanged)
    {
        _templatesDir = templatesDir;
        _log = log;
        _onStatsChanged = onStatsChanged;
    }

    public async Task RunLoopAsync(AppConfig config, CancellationToken ct)
    {
        var templates = LoadTemplates(config);

        _stats.Loops = 0;
        _stats.Claimed = 0;
        _stats.Dismantled = 0;
        UpdateStep("Bắt đầu quy trình...");

        int consecutiveFailures = 0;
        int longPauseMinLoops = Math.Max(1, config.LongPauseEveryMinLoops);
        int longPauseMaxLoops = Math.Max(longPauseMinLoops, config.LongPauseEveryMaxLoops);
        int loopsUntilLongPause = Random.Shared.Next(longPauseMinLoops, longPauseMaxLoops + 1);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int loopNum = _stats.Loops + 1;
                _log($"=== Bắt đầu vòng #{loopNum} ===");
                if (loopNum == 1)
                {
                    var capBounds = ScreenMatcher.ResolveCaptureBounds(config);
                    bool winFound = !string.IsNullOrWhiteSpace(config.TargetWindowTitle) &&
                                    Win32.FindWindowBoundsByTitle(config.TargetWindowTitle).HasValue;
                    _log($"[Thông tin] Vùng chụp {capBounds.Width}x{capBounds.Height} tại ({capBounds.X},{capBounds.Y}) - " +
                         (winFound ? "đang bám theo cửa sổ game." : "KHÔNG thấy cửa sổ game theo tiêu đề, đang chụp vùng tùy chỉnh/toàn màn hình."));
                }
                if (config.AutoFocusGameWindow && !string.IsNullOrWhiteSpace(config.TargetWindowTitle))
                    Win32.BringWindowToFrontByTitle(config.TargetWindowTitle);

                try
                {

                    // Bước 1: Nhấn Icon 1 (Tượng rồng)
                    UpdateStep($"Vòng #{loopNum} - Bước 1: Tìm & nhấn Icon 1");
                    var m1 = await WaitAndFindAsync(config, templates, TargetKey.Step1DragonIcon, ct);
                    await HumanMouse.MoveAndClickAsync(m1.ScreenBounds, config, fastMode: false, ct);
                    _log($"[Bước 1] Đã nhấn Icon 1 ({m1.Score:P0})");
                    await RandomStepDelayAsync(config, "Sau Bước 1", ct);

                    // Bước 2: Nhấn Rồng huyền thoại
                    UpdateStep($"Vòng #{loopNum} - Bước 2: Tìm & nhấn Rồng huyền thoại");
                    var m2 = await WaitAndFindAsync(config, templates, TargetKey.Step2LegendaryDragon, ct);
                    await HumanMouse.MoveAndClickAsync(m2.ScreenBounds, config, fastMode: false, ct);
                    _log($"[Bước 2] Đã nhấn Rồng huyền thoại ({m2.Score:P0})");
                    await RandomStepDelayAsync(config, "Sau Bước 2", ct);

                    // Bước 3: Nhấn BATTLE
                    UpdateStep($"Vòng #{loopNum} - Bước 3: Tìm & nhấn BATTLE");
                    var m3 = await WaitAndFindAsync(config, templates, TargetKey.Step3BattleButton, ct);
                    await ClickUntilGoneAsync(config, templates, TargetKey.Step3BattleButton, m3, 1, "BATTLE", ct);
                    _log($"[Bước 3] Đã nhấn BATTLE ({m3.Score:P0})");

                    // Bước 3.5 + 4: dò SONG SONG mini-game "Kim cương ở đâu?" và thanh màu xanh lam.
                    // Ngay sau BATTLE, mỗi lần quét cùng lúc tìm nút START và các thanh xanh, cái nào
                    // xuất hiện trước thì xử lý trước (không chờ cố định, không chờ tuần tự).
                    UpdateStep($"Vòng #{loopNum} - Bước 4: Dò mini-game & thanh màu xanh lam");
                    var blueBars = await WaitForBlueBarsOrMiniGameAsync(config, templates, loopNum, ct);
                    var orderedBars = OrderBlueBars(blueBars, config.BlueBarOrder);
                    _log($"[Bước 4] Tìm thấy {orderedBars.Count} thanh màu xanh lam. Bắt đầu nhấn mỗi thanh 1 lần...");

                    for (int i = 0; i < orderedBars.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await HumanMouse.MoveAndClickAsync(orderedBars[i].ScreenBounds, config, fastMode: true, ct);

                        if (i < orderedBars.Count - 1)
                        {
                            int minMs = Math.Max(10, config.BlueBarDelayMinMs);
                            int maxMs = Math.Max(minMs, config.BlueBarDelayMaxMs);
                            int delayMs = Random.Shared.Next(minMs, maxMs + 1);
                            await PreciseDelayMsAsync(delayMs, ct);
                        }
                    }
                    _log($"[Bước 4] Đã nhấn xong toàn bộ {orderedBars.Count} thanh màu xanh lam.");

                    // Bước 5: Chờ ~3 giây
                    double waitBattleSec = config.WaitAfterBlueBarsMinSeconds +
                        Random.Shared.NextDouble() * Math.Max(0, config.WaitAfterBlueBarsMaxSeconds - config.WaitAfterBlueBarsMinSeconds);
                    UpdateStep($"Vòng #{loopNum} - Bước 5: Chờ {waitBattleSec:F2}s");
                    _log($"[Bước 5] Chờ {waitBattleSec:F2}s...");
                    await Task.Delay(TimeSpan.FromSeconds(waitBattleSec), ct);

                    // Bước 6: Liên tục track trong 2 giây xem cả 2 dòng Trắng & Đỏ có xuất hiện không
                    double trackSec = Math.Max(0.3, config.LootTrackDurationSeconds);
                    UpdateStep($"Vòng #{loopNum} - Bước 6: Track Trắng & Đỏ ({trackSec:F1}s)");
                    var (foundBoth, claimMatch, dismantleMatch, scansCount) =
                        await TrackCooldownLinesAsync(config, templates, trackSec, ct);

                    if (foundBoth)
                    {
                        _log($"[Bước 6] Phát hiện CẢ 2 \"Giảm hồi chiêu\" (Trắng & Đỏ) sau {scansCount} lần quét -> Nhấn NHẬN!");
                        await ClickUntilGoneAsync(config, templates, TargetKey.Step6ClaimButton, claimMatch, 1, "NHẬN", ct);
                        _stats.Claimed++;
                        _log($"[Bước 6] -> Đã nhấn NHẬN! (Tổng nhận: {_stats.Claimed})");
                    }
                    else
                    {
                        _log($"[Bước 6] Hết {trackSec:F1}s ({scansCount} lần quét): Không thấy đủ 2 dòng Trắng & Đỏ -> Nhấn PHÂN RÃ 2 lần!");
                        await ClickUntilGoneAsync(config, templates, TargetKey.Step6DismantleButton, dismantleMatch, 2, "PHÂN RÃ", ct);

                        _stats.Dismantled++;
                        _log($"[Bước 6] -> Đã nhấn PHÂN RÃ 2 lần! (Tổng phân rã: {_stats.Dismantled})");
                    }

                    // Bước 7: Chờ ~0.1 giây rồi lặp lại từ bước 1
                    _stats.Loops++;
                    double repeatWaitSec = config.WaitBeforeRepeatMinSeconds +
                        Random.Shared.NextDouble() * Math.Max(0, config.WaitBeforeRepeatMaxSeconds - config.WaitBeforeRepeatMinSeconds);
                    int repeatWaitMs = Math.Max(20, (int)Math.Round(repeatWaitSec * 1000.0));
                    UpdateStep($"Xong vòng #{loopNum} - Chờ {repeatWaitSec:F2}s lặp lại");
                    _log($"[Bước 7] Hoàn tất vòng #{loopNum}. Chờ {repeatWaitSec:F2}s ({repeatWaitMs}ms) rồi lặp lại...");
                    await PreciseDelayMsAsync(repeatWaitMs, ct);

                    loopsUntilLongPause--;
                    if (loopsUntilLongPause <= 0)
                    {
                        double pauseSeconds = Math.Max(0, config.LongPauseDurationSeconds);
                        UpdateStep($"Nghỉ định kỳ {pauseSeconds:F1}s sau {longPauseMinLoops}-{longPauseMaxLoops} vòng");
                        _log($"[Nghỉ định kỳ] Đã hoàn tất khoảng nghỉ {longPauseMinLoops}-{longPauseMaxLoops} vòng. Tạm dừng {pauseSeconds:F1}s...");
                        await Task.Delay(TimeSpan.FromSeconds(pauseSeconds), ct);

                        loopsUntilLongPause = Random.Shared.Next(longPauseMinLoops, longPauseMaxLoops + 1);
                    }

                    consecutiveFailures = 0;
                }
                catch (TimeoutException ex)
                {
                    consecutiveFailures++;
                    _log($"[Cảnh báo] {ex.Message} (lỗi liên tiếp {consecutiveFailures}/{config.MaxConsecutiveFailures})");
                    if (consecutiveFailures >= Math.Max(1, config.MaxConsecutiveFailures))
                        throw;

                    UpdateStep($"Vòng #{loopNum} - Không thấy mục tiêu, thử lại từ bước 1...");
                    await PreciseDelayMsAsync(Random.Shared.Next(1200, 2200), ct);
                }
            }
        }
        finally
        {
            foreach (var bmp in templates.Values)
                bmp.Dispose();
            UpdateStep("Đã dừng");
        }
    }

    private sealed record MiniGameLayout(
        Point StartCenter,
        Rectangle? Diamond,
        bool FromDiamond,
        Point RingCenter,
        double Scale,
        List<Rectangle> SlotRects,
        Rectangle Ring,
        Rectangle TrackArea,
        bool InsideCapture);

    /// <summary>
    /// Works out where the 8 cups are. Before START is pressed the diamond sits in the middle of the ring:
    /// it gives the ring centre and the on-screen scale. Without it the layout is derived from the START button.
    /// Used by both the real mini-game handling and the test buttons, so a test shows exactly what the run will use.
    /// </summary>
    private static MiniGameLayout ComputeMiniGameLayout(AppConfig config, Rectangle captureBounds, Bitmap shot, Rectangle startBounds)
    {
        Point startCenter = new(startBounds.Left + startBounds.Width / 2, startBounds.Top + startBounds.Height / 2);

        var diamondSearch = new Rectangle(startCenter.X - 900, startCenter.Y - 650, 900, 630);
        Rectangle? diamond = ScreenMatcher.FindDiamondBounds(shot, captureBounds.Location, diamondSearch);

        Point ringCenter;
        double scale;
        if (diamond is { } d0)
        {
            scale = Math.Clamp(d0.Width / MiniGameDiamondRefWidth, 0.4, 4.0);
            ringCenter = new Point(d0.X + d0.Width / 2, d0.Y + d0.Height / 2);
        }
        else
        {
            scale = Math.Clamp(config.MiniGameLayoutScale, 0.3, 4.0);
            ringCenter = new Point(
                startCenter.X + (int)Math.Round(MiniGameRingFromStart.X * scale),
                startCenter.Y + (int)Math.Round(MiniGameRingFromStart.Y * scale));
        }

        int halfW = Math.Max(4, (int)Math.Round(MiniGameSlotHalfWidth * scale));
        int halfH = Math.Max(4, (int)Math.Round(MiniGameSlotHalfHeight * scale));
        var slotRects = new List<Rectangle>(MiniGameRingOffsets.Length);
        foreach (var off in MiniGameRingOffsets)
        {
            int cx = ringCenter.X + (int)Math.Round(off.X * scale);
            int cy = ringCenter.Y + (int)Math.Round(off.Y * scale);
            slotRects.Add(new Rectangle(cx - halfW, cy - halfH, halfW * 2, halfH * 2));
        }

        Rectangle ring = slotRects[0];
        foreach (var r in slotRects)
            ring = Rectangle.Union(ring, r);
        ring.Inflate(6, 6);

        bool inside = captureBounds.Contains(ring);

        // Area that is captured while the cups shuffle: the ring plus the room the cups fly through
        Rectangle trackArea = new(
            ringCenter.X - (int)Math.Round(215 * scale),
            ringCenter.Y - (int)Math.Round(205 * scale),
            (int)Math.Round(430 * scale),
            (int)Math.Round(420 * scale));
        trackArea = Rectangle.Intersect(captureBounds, Rectangle.Union(trackArea, ring));

        return new MiniGameLayout(startCenter, diamond, diamond != null, ringCenter, scale, slotRects, ring, trackArea, inside);
    }

    /// <summary>Result of <see cref="InspectMiniGame"/>: a text report and an annotated screenshot.</summary>
    public sealed class MiniGameTestReport
    {
        public bool Ok { get; set; }
        public List<string> Lines { get; } = new();
        public Bitmap? Image { get; set; }
    }

    /// <summary>
    /// Test 1 - detection only, nothing is clicked. With the mini-game open (START button visible) it checks:
    /// START template, diamond/ring/scale, the 8 cup positions, cup template + size, and how many of the 8
    /// cups the cup template recognises. Returns an annotated screenshot so the positions can be checked by eye.
    /// </summary>
    public MiniGameTestReport InspectMiniGame(AppConfig config, CancellationToken ct)
    {
        var report = new MiniGameTestReport();
        var startCfg = config.GetTarget(TargetKey.MiniGameStartButton);
        string startPath = Path.Combine(_templatesDir, startCfg.TemplateFileName);
        if (!File.Exists(startPath))
        {
            report.Lines.Add($"THIẾU ảnh mẫu nút START ({startCfg.TemplateFileName}). Chụp mẫu ở thẻ 'Mini-game START' trước.");
            return report;
        }

        using var startTpl = new Bitmap(startPath);
        Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
        var shot = ScreenMatcher.CaptureScreen(bounds);
        report.Image = shot;
        report.Lines.Add($"Vùng chụp: {bounds.Width}x{bounds.Height} tại ({bounds.X},{bounds.Y}).");

        var start = ScreenMatcher.FindBestMatch(shot, bounds.Location, startTpl, startCfg, ct);
        if (start == null)
        {
            var near = ScreenMatcher.FindBestMatch(shot, bounds.Location, startTpl, startCfg, ct, thresholdOverride: 0.40);
            report.Lines.Add("KHÔNG thấy nút START. Cần mở mini-game 'Kim cương ở đâu?' (chưa bấm START) rồi test lại.");
            report.Lines.Add(near != null
                ? $"Chỗ giống nhất chỉ đạt {near.Score:P0} (cần ≥ {startCfg.Threshold:P0}). Thử chụp lại ảnh mẫu START."
                : "Không có vùng nào giống nút START.");
            return report;
        }

        report.Lines.Add($"Thấy nút START ({start.Score:P0}).");

        var layout = ComputeMiniGameLayout(config, bounds, shot, start.ScreenBounds);
        report.Lines.Add(layout.FromDiamond
            ? $"Thấy kim cương ở giữa vòng: tỉ lệ {layout.Scale:F2}, tâm vòng ({layout.RingCenter.X},{layout.RingCenter.Y})."
            : $"KHÔNG thấy kim cương (có thể mini-game đã bấm START). Đang ước tính từ nút START, tỉ lệ {layout.Scale:F2}.");
        report.Lines.Add($"8 hộp: mỗi ô {layout.SlotRects[0].Width}x{layout.SlotRects[0].Height}px; " +
                         (layout.InsideCapture ? "nằm trong vùng chụp." : "NẰM NGOÀI vùng chụp (kiểm tra cửa sổ game / MiniGameLayoutScale)."));

        bool trackerOk = false;
        int cupsFound = -1;
        if (!config.MiniGameUseSpinTracker)
        {
            report.Lines.Add("Bộ theo dõi cốc quay đang TẮT (MiniGameUseSpinTracker = false).");
        }
        else if (layout.InsideCapture)
        {
            using var tracker = CreateSpinTracker(config, bounds, layout.SlotRects, layout.Scale, layout.TrackArea);
            if (tracker == null)
            {
                report.Lines.Add("Bộ theo dõi cốc quay KHÔNG khởi tạo được (xem log phía trên: thiếu cup_template.png hoặc lỗi OpenCV).");
            }
            else
            {
                using var frame = ScreenMatcher.CaptureScreen(layout.TrackArea);
                var probe = tracker.Probe(frame);
                cupsFound = probe.Cups;
                trackerOk = probe.Cups >= 8;
                report.Lines.Add($"Ảnh mẫu cái ly nhận ra {probe.Cups}/8 cốc (góc {probe.Angle}°, độ khớp TB {probe.MeanScore:P0}). " +
                                 (trackerOk ? "Tốt." : "CHƯA ĐỦ 8: ảnh mẫu hoặc kích thước chưa khớp, theo dõi có thể kém."));
            }
        }

        report.Ok = layout.InsideCapture && (!config.MiniGameUseSpinTracker || trackerOk);

        // Annotate
        using (var g = Graphics.FromImage(shot))
        {
            using var greenPen = new Pen(Color.Lime, 3f);
            using var cyanPen = new Pen(Color.Cyan, 3f);
            using var yellowPen = new Pen(Color.Yellow, 2.5f);
            using var magentaPen = new Pen(Color.Magenta, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold);

            Rectangle Rel(Rectangle r) => new(r.X - bounds.X, r.Y - bounds.Y, r.Width, r.Height);

            g.DrawRectangle(greenPen, Rel(start.ScreenBounds));
            g.DrawString("START", font, Brushes.Lime, start.ScreenBounds.X - bounds.X, Math.Max(0, start.ScreenBounds.Y - bounds.Y - 22));
            if (layout.Diamond is { } dm)
                g.DrawRectangle(cyanPen, Rel(dm));
            g.DrawRectangle(magentaPen, Rel(layout.TrackArea));
            for (int i = 0; i < layout.SlotRects.Count; i++)
            {
                var r = Rel(layout.SlotRects[i]);
                g.DrawRectangle(yellowPen, r);
                g.DrawString($"{i + 1}", font, Brushes.Yellow, r.X + 4, r.Y + 2);
            }

            int cxr = layout.RingCenter.X - bounds.X;
            int cyr = layout.RingCenter.Y - bounds.Y;
            g.DrawLine(cyanPen, cxr - 12, cyr, cxr + 12, cyr);
            g.DrawLine(cyanPen, cxr, cyr - 12, cxr, cyr + 12);
        }

        report.Lines.Add(report.Ok
            ? "KẾT LUẬN: nhận diện ổn. Ô vàng #1..#8 (từ hộp trên cùng, theo chiều kim đồng hồ) phải trùm đúng 8 hộp trong ảnh."
            : "KẾT LUẬN: chưa đạt, xem các dòng ở trên.");
        return report;
    }

    /// <summary>
    /// Test 2 - plays the mini-game once for real (moves the mouse and clicks), exactly like a normal run.
    /// Waits up to waitSeconds for the START button to appear, so it can be started before the mini-game shows.
    /// </summary>
    public async Task TestPlayMiniGameAsync(AppConfig config, int waitSeconds, CancellationToken ct)
    {
        var startCfg = config.GetTarget(TargetKey.MiniGameStartButton);
        string startPath = Path.Combine(_templatesDir, startCfg.TemplateFileName);
        if (!File.Exists(startPath))
            throw new FileNotFoundException($"Thiếu ảnh mẫu nút START ({startCfg.TemplateFileName}).", startPath);

        using var startTpl = new Bitmap(startPath);
        var templates = new Dictionary<TargetKey, Bitmap> { [TargetKey.MiniGameStartButton] = startTpl };

        _log($"[Test mini-game] Chờ nút START tối đa {waitSeconds}s (mở mini-game 'Kim cương ở đâu?' trong game)...");
        UpdateStep("Test mini-game: chờ nút START");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, waitSeconds));
        MatchResult? start = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var shot = ScreenMatcher.CaptureScreen(bounds);
            start = ScreenMatcher.FindBestMatch(shot, bounds.Location, startTpl, startCfg, ct);
            if (start != null)
                break;
            await Task.Delay(150, ct);
        }

        if (start == null)
        {
            _log("[Test mini-game] Hết thời gian chờ mà không thấy nút START. Hãy dùng nút 'Kiểm tra mini-game' để xem vì sao.");
            return;
        }

        await HandleMiniGameAsync(config, templates, start, 0, ct);
        UpdateStep("Test mini-game: xong");
        _log("[Test mini-game] Hoàn tất một lần chơi thử.");
    }

    private Dictionary<TargetKey, Bitmap> LoadTemplates(AppConfig config)
    {
        var dict = new Dictionary<TargetKey, Bitmap>();
        foreach (TargetKey key in Enum.GetValues<TargetKey>())
        {
            if (key == TargetKey.Step4BlueBar && config.BlueBarMode == BlueBarDetectMode.BlueColorBlob)
                continue;

            // Mini-game START button template is optional (mini-game may not appear)
            if (key == TargetKey.MiniGameStartButton && !config.MiniGameEnabled)
                continue;

            var cfg = config.GetTarget(key);
            string path = Path.Combine(_templatesDir, cfg.TemplateFileName);
            if (!File.Exists(path))
            {
                // Allow optional mini-game template to be missing
                if (key == TargetKey.MiniGameStartButton)
                {
                    _log($"[Lưu ý] Chưa có ảnh mẫu mini-game START. Sẽ bỏ qua mini-game khi chạy.");
                    continue;
                }
                throw new FileNotFoundException($"Thiếu ảnh mẫu cho '{cfg.DisplayName}'. Vui lòng chụp mẫu trước khi chạy.", path);
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            dict[key] = new Bitmap(fs);
        }
        return dict;
    }

    /// <summary>
    /// Called when a template could not be found: reports the best (below-threshold) similarity and saves
    /// a screenshot of exactly what the program captured to the "debug" folder next to the exe.
    /// </summary>
    private static string DiagnoseMiss(AppConfig config, Bitmap tpl, TargetConfig cfg)
    {
        try
        {
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);
            var best = ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, cfg, thresholdOverride: 0.40);

            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            string saved = "";
            if (Directory.GetFiles(dir, "miss_*.png").Length < 30)
            {
                string safe = string.Concat(cfg.TemplateFileName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                string file = Path.Combine(dir, $"miss_{safe}_{DateTime.Now:HHmmss}.png");
                screen.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                saved = $" Ảnh chụp: {file}";
            }

            string bestTxt = best != null
                ? $"giống nhất {best.Score:P0} (cần ≥ {cfg.Threshold:P0}) tại ({best.ScreenBounds.X},{best.ScreenBounds.Y})"
                : "không vùng nào giống quá 40%";
            return $" [Chẩn đoán] Vùng chụp {bounds.Width}x{bounds.Height}; {bestTxt}.{saved}";
        }
        catch
        {
            return "";
        }
    }

    private async Task<MatchResult> WaitAndFindAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        TargetKey key,
        CancellationToken ct)
    {
        var cfg = config.GetTarget(key);
        var tpl = templates[key];
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1500, config.WaitForMatchTimeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);
            var match = ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, cfg, ct);
            if (match != null)
                return match;

            await Task.Delay(180, ct);
        }

        string diag = DiagnoseMiss(config, tpl, cfg);
        throw new TimeoutException($"Không tìm thấy '{cfg.DisplayName}' trên màn hình sau {config.WaitForMatchTimeoutMs / 1000.0:F1}s.{diag}");
    }

    private static List<MatchResult> FindBlueBars(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        TargetConfig cfg,
        Bitmap screen,
        Rectangle bounds,
        CancellationToken ct)
    {
        return config.BlueBarMode == BlueBarDetectMode.BlueColorBlob
            ? ScreenMatcher.FindBlueBarBlobs(screen, bounds.Location, cfg.SearchRoi, ct)
            : ScreenMatcher.FindAllMatches(screen, bounds.Location, templates[TargetKey.Step4BlueBar], cfg, maxMatches: 64, ct: ct);
    }

    /// <summary>
    /// Scans the screen in ONE loop for both the mini-game START button and the blue bars.
    /// Whichever shows up first is handled first: if START appears the mini-game is played and the
    /// scan continues; as soon as blue bars are visible they are returned (no fixed waiting).
    /// </summary>
    private async Task<List<MatchResult>> WaitForBlueBarsOrMiniGameAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        int loopNum,
        CancellationToken ct)
    {
        var barCfg = config.GetTarget(TargetKey.Step4BlueBar);
        bool watchMiniGame = config.MiniGameEnabled && templates.ContainsKey(TargetKey.MiniGameStartButton);
        TargetConfig? startCfg = watchMiniGame ? config.GetTarget(TargetKey.MiniGameStartButton) : null;
        Bitmap? startTpl = watchMiniGame ? templates[TargetKey.MiniGameStartButton] : null;

        int timeoutMs = Math.Max(2000, config.WaitForMatchTimeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);

            var matches = FindBlueBars(config, templates, barCfg, screen, bounds, ct);

            // If 2+ blue bars are already visible, we are definitely in battle (mini-game has at most 1 blue diamond)
            if (matches.Count >= 2)
            {
                await Task.Delay(60, ct);
                Rectangle bounds2 = ScreenMatcher.ResolveCaptureBounds(config);
                using var screen2 = ScreenMatcher.CaptureScreen(bounds2);
                var stable = FindBlueBars(config, templates, barCfg, screen2, bounds2, ct);
                return stable.Count >= matches.Count ? stable : matches;
            }

            // Otherwise (0 or 1 blue blob), check if the mini-game START button is open
            if (watchMiniGame)
            {
                var start = ScreenMatcher.FindBestMatch(screen, bounds.Location, startTpl!, startCfg!, ct);
                if (start != null)
                {
                    await HandleMiniGameAsync(config, templates, start, loopNum, ct);
                    UpdateStep($"Vòng #{loopNum} - Bước 4: Dò thanh màu xanh lam");
                    deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    continue;
                }
            }

            if (matches.Count > 0)
            {
                await Task.Delay(60, ct);
                Rectangle bounds2 = ScreenMatcher.ResolveCaptureBounds(config);
                using var screen2 = ScreenMatcher.CaptureScreen(bounds2);

                if (watchMiniGame &&
                    ScreenMatcher.FindBestMatch(screen2, bounds2.Location, startTpl!, startCfg!, ct) != null)
                    continue;

                var stable = FindBlueBars(config, templates, barCfg, screen2, bounds2, ct);
                return stable.Count >= matches.Count ? stable : matches;
            }

            await Task.Delay(35, ct);
        }

        throw new TimeoutException($"Không tìm thấy thanh màu xanh lam nào sau {config.WaitForMatchTimeoutMs / 1000.0:F1}s.");
    }

    // Reference layout of the "Kim cương ở đâu?" panel, measured on a 788x594 screenshot (scale 1.0).
    // The 8 stumps sit on a ring around the diamond. Offsets are relative to the ring centre (= diamond),
    // numbered clockwise starting from the top stump (0 = top, 1 = top-right, 2 = right, ... 7 = top-left).
    private static readonly Point[] MiniGameRingOffsets =
    [
        new(0, -121),    // 0 top
        new(91, -91),    // 1 top-right
        new(121, 0),     // 2 right
        new(91, 90),     // 3 bottom-right
        new(0, 120),     // 4 bottom
        new(-91, 90),    // 5 bottom-left
        new(-121, 0),    // 6 left
        new(-91, -91)    // 7 top-left
    ];
    private const int MiniGameSlotHalfWidth = 28;
    private const int MiniGameSlotHalfHeight = 40;
    private const double MiniGameDiamondRefWidth = 43.0;       // width of the diamond's light-blue fill at scale 1.0
    private static readonly Point MiniGameRingFromStart = new(-288, -220); // ring centre relative to START (fallback)

    /// <summary>
    /// Plays the "Kim cương ở đâu?" mini-game once its START button has been detected.
    /// The game is a shell game: the diamond drops into one stump, then all 8 stumps tumble into a jumble in
    /// the middle and spread back out to the ring. We (1) find the ring from the diamond, (2) note which stump
    /// the diamond drops into, (3) wait until the shuffle has finished and the ring is still again, and
    /// (4) click the stump chosen from MiniGameSlotMap (or a random one when no rule is known yet).
    /// </summary>
    private async Task HandleMiniGameAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        MatchResult startMatch,
        int loopNum,
        CancellationToken ct)
    {
        var startCfg = config.GetTarget(TargetKey.MiniGameStartButton);
        var startTpl = templates[TargetKey.MiniGameStartButton];

        _log($"[Mini-game] Phát hiện mini-game \"Kim cương ở đâu?\" (START: {startMatch.Score:P0})");
        UpdateStep($"Vòng #{loopNum} - Mini-game: Xác định vị trí 8 hộp");

        Rectangle captureBounds = ScreenMatcher.ResolveCaptureBounds(config);
        MiniGameLayout layout;
        using (var shot = ScreenMatcher.CaptureScreen(captureBounds))
            layout = ComputeMiniGameLayout(config, captureBounds, shot, startMatch.ScreenBounds);

        _log(layout.FromDiamond
            ? $"[Mini-game] Thấy kim cương ở giữa vòng hộp (tỉ lệ {layout.Scale:F2})."
            : "[Mini-game] Không thấy kim cương, tính vị trí 8 hộp từ nút START.");

        Point ringCenter = layout.RingCenter;
        double scale = layout.Scale;
        List<Rectangle> slotRects = layout.SlotRects;
        Rectangle ring = layout.Ring;
        Rectangle trackArea = layout.TrackArea;

        if (!layout.InsideCapture)
        {
            _log("[Mini-game] Vùng 8 hộp nằm ngoài cửa sổ game. Bỏ qua mini-game.");
            return;
        }

        // The spinning-cup tracker needs the on-screen size of one cup: measure the isolated top and bottom cups
        using var tracker = config.MiniGameUseSpinTracker
            ? CreateSpinTracker(config, captureBounds, slotRects, scale, trackArea)
            : null;

        // Click the START button
        UpdateStep($"Vòng #{loopNum} - Mini-game: Nhấn START");
        await HumanMouse.MoveAndClickAsync(startMatch.ScreenBounds, config, fastMode: false, ct);
        _log("[Mini-game] Đã nhấn START. Theo dõi kim cương rơi vào hộp nào...");

        int intervalMs = Math.Max(25, config.MiniGameSampleIntervalMs);
        int slotCount = slotRects.Count;

        // Optional: save every captured frame of this mini-game so the shuffle can be analysed offline
        string? recordDir = null;
        int recorded = 0;
        if (config.MiniGameSaveFrames)
        {
            try
            {
                recordDir = Path.Combine(AppContext.BaseDirectory, "debug", $"minigame_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(recordDir);
                _log($"[Mini-game] Đang lưu khung hình vào: {recordDir}");
            }
            catch
            {
                recordDir = null;
            }
        }

        void Record(Bitmap frame)
        {
            if (recordDir == null || recorded >= 400)
                return;
            try
            {
                frame.Save(Path.Combine(recordDir, $"f_{recorded++:D3}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch
            {
                recordDir = null;
            }
        }

        // ---- Phase 1: follow the diamond until it disappears into a stump ----
        UpdateStep($"Vòng #{loopNum} - Mini-game: Theo dõi kim cương");
        Point? lastDiamond = null;
        int missing = 0;
        var dropDeadline = DateTime.UtcNow.AddMilliseconds(4000);
        while (DateTime.UtcNow < dropDeadline)
        {
            ct.ThrowIfCancellationRequested();
            using (var frame = ScreenMatcher.CaptureScreen(ring))
            {
                Record(frame);
                var d = ScreenMatcher.FindDiamondBounds(frame, ring.Location, ring);
                if (d is { } db)
                {
                    lastDiamond = new Point(db.X + db.Width / 2, db.Y + db.Height / 2);
                    missing = 0;
                }
                else if (lastDiamond != null && ++missing >= 3)
                {
                    break;
                }
            }

            await Task.Delay(intervalMs, ct);
        }

        // The diamond fades out as it enters the stump, so its last visible position is still short of the
        // stump. Choose the stump by DIRECTION from the ring centre (it drops in a straight line).
        int dropSlot = -1;
        if (lastDiamond is { } ld &&
            Math.Abs(ld.X - ringCenter.X) + Math.Abs(ld.Y - ringCenter.Y) > 30 * scale)
        {
            double vx = ld.X - ringCenter.X;
            double vy = ld.Y - ringCenter.Y;
            double vLen = Math.Sqrt(vx * vx + vy * vy);
            double bestCos = double.MinValue;
            for (int i = 0; i < slotCount; i++)
            {
                double sx = MiniGameRingOffsets[i].X;
                double sy = MiniGameRingOffsets[i].Y;
                double cos = (vx * sx + vy * sy) / (vLen * Math.Sqrt(sx * sx + sy * sy));
                if (cos > bestCos)
                {
                    bestCos = cos;
                    dropSlot = i;
                }
            }
        }

        _log(dropSlot >= 0
            ? $"[Mini-game] Kim cương rơi vào hộp #{dropSlot + 1} (đánh số theo chiều kim đồng hồ, #1 = hộp trên cùng)."
            : "[Mini-game] Không xác định được hộp mà kim cương rơi vào.");

        // ---- Phase 2: follow the diamond's cup through the shuffle until all cups are back in the ring ----
        UpdateStep($"Vòng #{loopNum} - Mini-game: Theo dõi cốc có kim cương");
        var relRois = slotRects
            .Select(r => new Rectangle(r.X - trackArea.X, r.Y - trackArea.Y, r.Width, r.Height))
            .ToList();

        if (tracker != null && dropSlot >= 0)
        {
            tracker.Start(
                slotRects[dropSlot].X + slotRects[dropSlot].Width / 2.0,
                slotRects[dropSlot].Y + slotRects[dropSlot].Height / 2.0);
        }
        bool trackerOk = tracker != null && dropSlot >= 0;
        string lastTrackStatus = "";
        int pixelThreshold = Math.Max(8, config.MiniGamePixelThreshold);
        double activeRatio = Math.Clamp(config.MiniGameActiveRatio, 0.005, 0.9);
        int stillNeeded = Math.Max(3, config.MiniGameStableFrames);

        bool leftRing = false;   // at least one stump has left its slot (shuffle really started)
        bool settled = false;
        int stillFrames = 0;
        var settleDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(3000, config.MiniGameTrackTimeoutMs));

        Bitmap? prev = ScreenMatcher.CaptureScreen(trackArea);
        try
        {
            while (DateTime.UtcNow < settleDeadline)
            {
                ct.ThrowIfCancellationRequested();
                if (trackerOk)
                    await Task.Yield();   // the tracker itself takes a few ms per frame; do not add idle time
                else
                    await Task.Delay(intervalMs, ct);

                var cur = ScreenMatcher.CaptureScreen(trackArea);
                Record(cur);

                if (trackerOk)
                {
                    try
                    {
                        lastTrackStatus = tracker!.Update(cur);
                    }
                    catch (Exception ex)
                    {
                        trackerOk = false;
                        _log($"[Mini-game] Bộ theo dõi bị lỗi ({ex.GetType().Name}: {ex.Message}). Chuyển sang cách dự phòng.");
                    }
                }

                double[] motion = ScreenMatcher.MeasureMotionRatios(prev, cur, relRois, pixelThreshold);
                double[] occupancy = ScreenMatcher.MeasureStumpOccupancy(cur, relRois);
                prev.Dispose();
                prev = cur;

                bool allPresent = occupancy.All(o => o >= 0.30);
                bool anyMoving = motion.Any(m => m >= activeRatio);

                if (!allPresent)
                    leftRing = true;

                if (leftRing && allPresent && !anyMoving)
                {
                    if (++stillFrames >= stillNeeded)
                    {
                        settled = true;
                        break;
                    }
                }
                else
                {
                    stillFrames = 0;
                }
            }
        }
        finally
        {
            prev?.Dispose();
        }

        _log(settled
            ? "[Mini-game] Xáo trộn xong, 8 hộp đã về vị trí."
            : "[Mini-game] Hết thời gian chờ xáo trộn, vẫn tiếp tục nhấn.");

        // ---- Phase 3: pick the stump ----
        int target = -1;
        string how = "";

        if (trackerOk && tracker != null && tracker.Phase != CupSpinTracker.TrackPhase.Idle)
        {
            // The cup that spun is the one holding the diamond: click the slot where it ended up
            double bestDist = double.MaxValue;
            for (int i = 0; i < slotCount; i++)
            {
                double dx = tracker.X - (slotRects[i].X + slotRects[i].Width / 2.0);
                double dy = tracker.Y - (slotRects[i].Y + slotRects[i].Height / 2.0);
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    target = i;
                }
            }

            how = $"theo dõi cốc quay: bắt đầu ở hộp #{dropSlot + 1}, dừng cách tâm hộp {bestDist:F0}px, " +
                  $"{tracker.Frames} khung, mất dấu {tracker.TotalMisses} lần, pha cuối: {tracker.Phase}";
            if (bestDist > 0.9 * tracker.CupWidth || tracker.Phase != CupSpinTracker.TrackPhase.Aligned)
                how += " - ĐỘ TIN CẬY THẤP";
        }
        else
        {
            if (tracker != null && trackerOk)
                _log("[Mini-game] Không thấy cốc nào quay khác nhóm, dùng cách dự phòng.");

            var map = config.MiniGameSlotMap;
            if (dropSlot >= 0 && map != null && map.Length == slotCount && map[dropSlot] >= 0 && map[dropSlot] < slotCount)
            {
                target = map[dropSlot];
                how = "theo bảng MiniGameSlotMap";
            }
            else
            {
                target = Random.Shared.Next(slotCount);
                how = "ngẫu nhiên vì không theo dõi được";
            }
        }

        _log($"[Mini-game] Nhấn hộp #{target + 1} ({how}).");
        if (recordDir != null)
        {
            try
            {
                File.WriteAllText(Path.Combine(recordDir, "info.txt"),
                    $"ring={ring}\ntrackArea={trackArea}\nscale={scale:F2}\ndiamondDropSlot(0=top clockwise)={dropSlot}\nclickedSlot={target}\nhow={how}\nframes={recorded}\n");
            }
            catch { }
        }
        UpdateStep($"Vòng #{loopNum} - Mini-game: Nhấn hộp #{target + 1}");
        await Task.Delay(Random.Shared.Next(150, 400), ct);
        await HumanMouse.MoveAndClickAsync(slotRects[target], config, fastMode: false, ct);
        _log("[Mini-game] Đã nhấn hộp.");

        // Wait for the result, then verify the mini-game panel has closed (START gone)
        await Task.Delay(Random.Shared.Next(800, 1400), ct);

        var closeDeadline = DateTime.UtcNow.AddMilliseconds(2500);
        bool stillOpen = true;
        while (DateTime.UtcNow < closeDeadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);
            stillOpen = ScreenMatcher.FindBestMatch(screen, bounds.Location, startTpl, startCfg, ct) != null;
            if (!stillOpen)
                break;
            await Task.Delay(200, ct);
        }

        _log(stillOpen
            ? "[Mini-game] Cảnh báo: nút START vẫn còn trên màn hình, mini-game có thể chưa đóng."
            : "[Mini-game] Mini-game đã đóng. Hoàn tất.");

        return;
    }

    /// <summary>
    /// Creates the spinning-cup tracker: measures one isolated cup (top and bottom of the ring) on screen so
    /// the cup template can be scaled to the real size. Returns null (and logs why) when it cannot be used.
    /// </summary>
    private CupSpinTracker? CreateSpinTracker(
        AppConfig config,
        Rectangle captureBounds,
        List<Rectangle> slotRects,
        double scale,
        Rectangle trackArea)
    {
        try
        {
            double cupW = 0;
            double cupArea = 0;
            var widths = new List<double>();
            var areas = new List<double>();

            using (var shot = ScreenMatcher.CaptureScreen(captureBounds))
            {
                foreach (int idx in new[] { 0, 4 }) // top and bottom cups: no neighbour within the crop
                {
                    var c = slotRects[idx];
                    int cx = c.X + c.Width / 2 - captureBounds.X;
                    int cy = c.Y + c.Height / 2 - captureBounds.Y;
                    var roi = new Rectangle(
                        cx - (int)Math.Round(44 * scale),
                        cy - (int)Math.Round(62 * scale),
                        (int)Math.Round(88 * scale),
                        (int)Math.Round(124 * scale));
                    if (CupSpinTracker.MeasureCup(shot, roi, out double w, out double a))
                    {
                        widths.Add(w);
                        areas.Add(a);
                    }
                }
            }

            if (widths.Count > 0)
            {
                cupW = widths.Average();
                cupArea = areas.Average();
            }
            else
            {
                cupW = 55 * scale;          // fallback measured on the reference layout
                cupArea = 3340 * scale * scale;
                _log("[Mini-game] Không đo được kích thước cái ly, dùng kích thước ước tính.");
            }

            string templatePath = Path.Combine(_templatesDir, config.MiniGameCupTemplate);
            var tracker = CupSpinTracker.TryCreate(templatePath, cupW, cupArea, trackArea.X, trackArea.Y, out string error);
            if (tracker == null)
            {
                _log($"[Mini-game] {error}. Không theo dõi được cốc quay, dùng cách dự phòng.");
                return null;
            }

            _log($"[Mini-game] Bộ theo dõi cốc quay sẵn sàng (cốc rộng ~{cupW:F0}px).");
            return tracker;
        }
        catch (Exception ex)
        {
            _log($"[Mini-game] Không tạo được bộ theo dõi cốc quay: {ex.Message}");
            return null;
        }
    }

    private static MatchResult? FindNow(AppConfig config, Bitmap tpl, TargetConfig cfg, CancellationToken ct)
    {
        Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
        using var screen = ScreenMatcher.CaptureScreen(bounds);
        return ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, cfg, ct);
    }

    private static bool IsStillVisibleAt(Rectangle localBounds, Bitmap tpl, TargetConfig cfg, CancellationToken ct)
    {
        var probeRect = localBounds;
        probeRect.Inflate(16, 16);
        var vs = Win32.GetVirtualScreenBounds();
        probeRect = Rectangle.Intersect(vs, probeRect);
        if (probeRect.Width < tpl.Width || probeRect.Height < tpl.Height)
            return false;

        using var crop = ScreenMatcher.CaptureScreen(probeRect);
        var localCfg = new TargetConfig
        {
            Key = cfg.Key,
            Threshold = cfg.Threshold,
            RequireColorMatch = cfg.RequireColorMatch,
            SearchRoi = null
        };
        return ScreenMatcher.FindBestMatch(crop, probeRect.Location, tpl, localCfg, ct) != null;
    }

    /// <summary>
    /// Clicks a button and checks that it disappeared. If it is still on screen the click did not
    /// register (game not focused, click too fast...), so click again, up to ClickRetryMax times.
    /// The button position is refreshed right before retry attempts.
    /// </summary>
    private async Task<MatchResult> ClickUntilGoneAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        TargetKey key,
        MatchResult? knownMatch,
        int clicksPerAttempt,
        string label,
        CancellationToken ct)
    {
        var cfg = config.GetTarget(key);
        var tpl = templates[key];
        var target = knownMatch ?? await WaitAndFindAsync(config, templates, key, ct);
        int maxAttempts = config.VerifyClicks ? Math.Max(1, config.ClickRetryMax) : 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                if (config.AutoFocusGameWindow && !string.IsNullOrWhiteSpace(config.TargetWindowTitle))
                    Win32.BringWindowToFrontByTitle(config.TargetWindowTitle);

                var fresh = FindNow(config, tpl, cfg, ct);
                if (fresh != null)
                    target = fresh;
            }

            for (int c = 0; c < clicksPerAttempt; c++)
            {
                await HumanMouse.MoveAndClickAsync(target.ScreenBounds, config, fastMode: c > 0, ct);
                if (c < clicksPerAttempt - 1)
                    await PreciseDelayMsAsync(Random.Shared.Next(110, 210), ct);
            }

            if (!config.VerifyClicks)
                return target;

            // Fast local check (~1-2ms) until the button disappears
            var verifyDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(300, config.ClickVerifyTimeoutMs));
            while (DateTime.UtcNow < verifyDeadline)
            {
                await Task.Delay(35, ct);
                if (!IsStillVisibleAt(target.ScreenBounds, tpl, cfg, ct))
                    return target;
            }

            if (attempt < maxAttempts)
                _log($"[Thử lại] Nút {label} vẫn còn sau khi nhấn (lần {attempt}/{maxAttempts}). Nhấn lại...");
        }

        _log($"[Cảnh báo] Nút {label} vẫn còn sau {maxAttempts} lần nhấn. Tiếp tục quy trình.");
        return target;
    }

    private async Task<(bool FoundBoth, MatchResult? Claim, MatchResult? Dismantle, int ScansCount)>
        TrackCooldownLinesAsync(
            AppConfig config,
            Dictionary<TargetKey, Bitmap> templates,
            double trackDurationSeconds,
            CancellationToken ct)
    {
        var whiteCfg = config.GetTarget(TargetKey.Step6CooldownWhite);
        var redCfg = config.GetTarget(TargetKey.Step6CooldownRed);

        var deadline = DateTime.UtcNow.AddSeconds(trackDurationSeconds);
        MatchResult? lastClaim = null;
        MatchResult? lastDismantle = null;
        int scans = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            scans++;

            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);

            var whiteCd = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6CooldownWhite], whiteCfg, ct);
            var redCd = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6CooldownRed], redCfg, ct);


            if (whiteCd != null && redCd != null)
            {
                _log($"   [Track #{scans}] Trắng ({whiteCd.Score:P0}) + Đỏ ({redCd.Score:P0}) cùng xuất hiện!");
                return (true, lastClaim, lastDismantle, scans);
            }

            if (DateTime.UtcNow >= deadline)
                break;

            await Task.Delay(45, ct);
        }

        return (false, lastClaim, lastDismantle, scans);
    }

    private static List<MatchResult> OrderBlueBars(List<MatchResult> bars, BlueBarClickOrder order)
    {
        if (bars.Count <= 1)
            return bars;

        switch (order)
        {
            case BlueBarClickOrder.LeftToRight:
                return bars.OrderBy(b => b.ScreenBounds.Left).ThenBy(b => b.ScreenBounds.Top).ToList();

            case BlueBarClickOrder.TopToBottom:
                return bars.OrderBy(b => b.ScreenBounds.Top).ThenBy(b => b.ScreenBounds.Left).ToList();

            case BlueBarClickOrder.FullyShuffled:
                return bars.OrderBy(_ => Random.Shared.Next()).ToList();

            case BlueBarClickOrder.NaturalRandomized:
            default:
                // Nearest-neighbor traversal from current cursor (or random starter) with random tie-break noise
                var remaining = new List<MatchResult>(bars);
                var ordered = new List<MatchResult>(bars.Count);
                Point cur = Win32.GetMousePosition();

                // 35% chance to pick a random starting bar near top-left instead of nearest to cursor
                if (Random.Shared.NextDouble() < 0.35)
                {
                    int starterIdx = Random.Shared.Next(Math.Min(3, remaining.Count));
                    var first = remaining[starterIdx];
                    ordered.Add(first);
                    remaining.RemoveAt(starterIdx);
                    cur = new Point(first.ScreenBounds.Left + first.ScreenBounds.Width / 2,
                                    first.ScreenBounds.Top + first.ScreenBounds.Height / 2);
                }

                while (remaining.Count > 0)
                {
                    int bestIdx = 0;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var r = remaining[i].ScreenBounds;
                        double cx = r.Left + r.Width * 0.5;
                        double cy = r.Top + r.Height * 0.5;
                        double d = Math.Sqrt((cx - cur.X) * (cx - cur.X) + (cy - cur.Y) * (cy - cur.Y));
                        // Add +-18% randomness so path isn't identical every loop
                        double noisyDist = d * (0.82 + Random.Shared.NextDouble() * 0.36);
                        if (noisyDist < bestDist)
                        {
                            bestDist = noisyDist;
                            bestIdx = i;
                        }
                    }

                    var chosen = remaining[bestIdx];
                    ordered.Add(chosen);
                    remaining.RemoveAt(bestIdx);
                    cur = new Point(chosen.ScreenBounds.Left + chosen.ScreenBounds.Width / 2,
                                    chosen.ScreenBounds.Top + chosen.ScreenBounds.Height / 2);
                }

                return ordered;
        }
    }

    private async Task RandomStepDelayAsync(AppConfig config, string label, CancellationToken ct)
    {
        // Math.Random() + 0.3 seconds (default: 0.3s + [0..1.0)s)
        double sec = config.StepDelayMinSeconds + Random.Shared.NextDouble() * config.StepDelayRandomSpanSeconds;
        int ms = Math.Max(50, (int)Math.Round(sec * 1000.0));
        _log($"   Chờ giữa bước ({label}): {sec:F3}s ({ms}ms)");
        await PreciseDelayMsAsync(ms, ct);
    }

    private static async Task PreciseDelayMsAsync(int ms, CancellationToken ct)
    {
        Win32.timeBeginPeriod(1);
        try
        {
            await Task.Delay(ms, ct);
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    private void UpdateStep(string step)
    {
        _stats.CurrentStep = step;
        _onStatsChanged(new RunStats
        {
            Loops = _stats.Loops,
            Claimed = _stats.Claimed,
            Dismantled = _stats.Dismantled,
            CurrentStep = _stats.CurrentStep
        });
    }
}
