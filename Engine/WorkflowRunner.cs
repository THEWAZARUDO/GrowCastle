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

        throw new TimeoutException($"Không tìm thấy '{cfg.DisplayName}' trên màn hình sau {config.WaitForMatchTimeoutMs / 1000.0:F1}s.");
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

    // Reference layout of the "Kim cương ở đâu?" panel, measured on a 788x594 screenshot.
    // Every offset is the centre of one of the 8 stumps relative to the centre of the START button.
    private static readonly Point[] MiniGameSlotOffsets =
    [
        new(-288, -341), // top
        new(-197, -311), // top-right
        new(-167, -220), // right
        new(-197, -130), // bottom-right
        new(-288, -100), // bottom
        new(-379, -130), // bottom-left
        new(-409, -220), // left
        new(-379, -311)  // top-left
    ];
    private const int MiniGameSlotHalfWidth = 28;
    private const int MiniGameSlotHalfHeight = 40;

    /// <summary>
    /// Plays the "Kim cương ở đâu?" mini-game once its START button has been detected:
    /// clicks START, then watches each of the 8 stumps separately and clicks the one that keeps spinning.
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
        UpdateStep($"Vòng #{loopNum} - Mini-game: Nhấn START");

        // Work out where the 8 stumps are from the START button position (before the cursor moves there)
        double scale = Math.Clamp(config.MiniGameLayoutScale, 0.3, 4.0);
        Point startCenter = new(
            startMatch.ScreenBounds.Left + startMatch.ScreenBounds.Width / 2,
            startMatch.ScreenBounds.Top + startMatch.ScreenBounds.Height / 2);
        int halfW = Math.Max(4, (int)Math.Round(MiniGameSlotHalfWidth * scale));
        int halfH = Math.Max(4, (int)Math.Round(MiniGameSlotHalfHeight * scale));

        var slotRects = new List<Rectangle>(MiniGameSlotOffsets.Length);
        foreach (var off in MiniGameSlotOffsets)
        {
            int cx = startCenter.X + (int)Math.Round(off.X * scale);
            int cy = startCenter.Y + (int)Math.Round(off.Y * scale);
            slotRects.Add(new Rectangle(cx - halfW, cy - halfH, halfW * 2, halfH * 2));
        }

        Rectangle captureBounds = ScreenMatcher.ResolveCaptureBounds(config);
        Rectangle ring = slotRects[0];
        foreach (var r in slotRects)
            ring = Rectangle.Union(ring, r);
        ring.Inflate(4, 4);

        // Click the START button
        await HumanMouse.MoveAndClickAsync(startMatch.ScreenBounds, config, fastMode: false, ct);

        if (!captureBounds.Contains(ring))
        {
            _log("[Mini-game] Vùng 8 hộp nằm ngoài cửa sổ game (kiểm tra MiniGameLayoutScale). Bỏ qua mini-game.");
            return;
        }

        _log("[Mini-game] Đã nhấn START. Chờ xáo trộn...");
        int shuffleWait = config.MiniGameShuffleWaitMs + Random.Shared.Next(0, 500);
        await Task.Delay(shuffleWait, ct);

        UpdateStep($"Vòng #{loopNum} - Mini-game: Tìm hộp quay...");

        // ROIs relative to the captured ring area
        var relRois = slotRects
            .Select(r => new Rectangle(r.X - ring.X, r.Y - ring.Y, r.Width, r.Height))
            .ToList();

        int slotCount = relRois.Count;
        int intervalMs = Math.Max(30, config.MiniGameSampleIntervalMs);
        int pixelThreshold = Math.Max(8, config.MiniGamePixelThreshold);
        double activeRatio = Math.Clamp(config.MiniGameActiveRatio, 0.005, 0.9);
        int stableFrames = Math.Max(2, config.MiniGameStableFrames);
        int windowSize = stableFrames + 2;

        // The correct stump spins in place for many turns, then swaps to another slot and keeps spinning.
        // So we only look at the most recent frames: for each frame pair we record which stump was the
        // ONLY one moving (-1 = nothing moving, -2 = several moving, e.g. during a swap).
        var window = new Queue<int>();
        int frames = 0;
        int chosen = -1;

        var trackDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1500, config.MiniGameTrackTimeoutMs));
        _log($"[Mini-game] Theo dõi 8 hộp (tối đa {config.MiniGameTrackTimeoutMs / 1000.0:F1}s)...");

        Bitmap? prev = ScreenMatcher.CaptureScreen(ring);
        try
        {
            while (DateTime.UtcNow < trackDeadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(intervalMs, ct);

                var cur = ScreenMatcher.CaptureScreen(ring);
                double[] ratios = ScreenMatcher.MeasureMotionRatios(prev, cur, relRois, pixelThreshold);
                prev.Dispose();
                prev = cur;

                frames++;
                window.Enqueue(ClassifyMotion(ratios, activeRatio));
                while (window.Count > windowSize)
                    window.Dequeue();

                if (frames % 6 == 0)
                {
                    string summary = string.Join(" ", ratios.Select(r => (r * 100).ToString("F0")));
                    _log($"   [Motion #{frames}] % thay đổi 8 hộp: {summary}");
                }

                // Candidate = the stump that was the only moving one in most of the recent frames
                int cand = -1;
                int candCount = 0;
                for (int i = 0; i < slotCount; i++)
                {
                    int c = window.Count(v => v == i);
                    if (c > candCount)
                    {
                        candCount = c;
                        cand = i;
                    }
                }

                if (cand < 0 || candCount < stableFrames)
                    continue;

                // Move onto it first, then make sure it is STILL spinning (it may have just swapped away)
                _log($"[Mini-game] Hộp #{cand + 1} đang quay ({candCount}/{window.Count} khung gần nhất). Di chuột tới...");
                Point target = HumanMouse.PickHumanPointInRect(slotRects[cand], 0.25);
                await HumanMouse.MoveToAsync(target, config, fastMode: false, ct);

                bool stillSpinning = await ConfirmSpinnerAsync(
                    ring, relRois, cand, intervalMs, pixelThreshold, activeRatio, ct);

                if (stillSpinning)
                {
                    await HumanMouse.ClickAsync(fastMode: false, ct);
                    chosen = cand;
                    break;
                }

                _log($"[Mini-game] Hộp #{cand + 1} không còn quay (có thể vừa đổi chỗ). Theo dõi lại...");
                window.Clear();
                prev.Dispose();
                prev = ScreenMatcher.CaptureScreen(ring);
            }
        }
        finally
        {
            prev?.Dispose();
        }

        if (chosen < 0)
        {
            _log("[Mini-game] Không tìm thấy hộp nào quay liên tục. Bỏ qua mini-game.");
            return;
        }

        _log($"[Mini-game] Đã nhấn hộp quay #{chosen + 1}.");

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
    /// Returns the index of the only moving stump, -1 if none is moving, -2 if several are moving.
    /// </summary>
    private static int ClassifyMotion(double[] ratios, double activeRatio)
    {
        int activeCount = 0;
        int activeIdx = -1;
        for (int i = 0; i < ratios.Length; i++)
        {
            if (ratios[i] >= activeRatio)
            {
                activeCount++;
                activeIdx = i;
            }
        }

        return activeCount switch
        {
            0 => -1,
            1 => activeIdx,
            _ => -2
        };
    }

    /// <summary>
    /// Watches a few more frames (mouse already resting on the stump) and confirms that
    /// this stump is still the only one spinning.
    /// </summary>
    private static async Task<bool> ConfirmSpinnerAsync(
        Rectangle ring,
        IReadOnlyList<Rectangle> relRois,
        int slot,
        int intervalMs,
        int pixelThreshold,
        double activeRatio,
        CancellationToken ct)
    {
        int hits = 0;
        Bitmap prev = ScreenMatcher.CaptureScreen(ring);
        try
        {
            for (int k = 0; k < 3; k++)
            {
                await Task.Delay(intervalMs, ct);
                var cur = ScreenMatcher.CaptureScreen(ring);
                double[] ratios = ScreenMatcher.MeasureMotionRatios(prev, cur, relRois, pixelThreshold);
                prev.Dispose();
                prev = cur;

                if (ClassifyMotion(ratios, activeRatio) == slot)
                    hits++;
            }
        }
        finally
        {
            prev.Dispose();
        }

        return hits >= 2;
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
