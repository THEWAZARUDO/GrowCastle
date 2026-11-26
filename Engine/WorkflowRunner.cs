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

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int loopNum = _stats.Loops + 1;
                _log($"=== Bắt đầu vòng #{loopNum} ===");

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
                await HumanMouse.MoveAndClickAsync(m3.ScreenBounds, config, fastMode: false, ct);
                _log($"[Bước 3] Đã nhấn BATTLE ({m3.Score:P0})");
                await RandomStepDelayAsync(config, "Sau Bước 3", ct);

                // Bước 3.5: Kiểm tra mini-game "Kim cương ở đâu?"
                if (config.MiniGameEnabled)
                {
                    bool handled = await TryHandleMiniGameAsync(config, templates, loopNum, ct);
                    if (handled)
                        await RandomStepDelayAsync(config, "Sau mini-game", ct);
                }

                // Bước 4: Tìm và nhấn toàn bộ thanh màu xanh lam (~50ms + random)
                UpdateStep($"Vòng #{loopNum} - Bước 4: Đang tìm các thanh màu xanh lam");
                if (config.BlueBarAppearDelayMs > 0)
                {
                    int appearWait = config.BlueBarAppearDelayMs + Random.Shared.Next(0, 180);
                    await Task.Delay(appearWait, ct);
                }

                var blueBars = await WaitAndFindAllBlueBarsAsync(config, templates, ct);
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
                    claimMatch ??= await WaitAndFindAsync(config, templates, TargetKey.Step6ClaimButton, ct);

                    await HumanMouse.MoveAndClickAsync(claimMatch.ScreenBounds, config, fastMode: false, ct);
                    _stats.Claimed++;
                    _log($"[Bước 6] -> Đã nhấn NHẬN! (Tổng nhận: {_stats.Claimed})");
                }
                else
                {
                    _log($"[Bước 6] Hết {trackSec:F1}s ({scansCount} lần quét): Không thấy đủ 2 dòng Trắng & Đỏ -> Nhấn PHÂN RÃ 2 lần!");
                    dismantleMatch ??= await WaitAndFindAsync(config, templates, TargetKey.Step6DismantleButton, ct);

                    await HumanMouse.MoveAndClickAsync(dismantleMatch.ScreenBounds, config, fastMode: false, ct);
                    int doubleClickGapMs = Random.Shared.Next(110, 210);
                    await PreciseDelayMsAsync(doubleClickGapMs, ct);
                    await HumanMouse.MoveAndClickAsync(dismantleMatch.ScreenBounds, config, fastMode: true, ct);

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
            var match = ScreenMatcher.FindBestMatch(screen, bounds.Location, tpl, cfg);
            if (match != null)
                return match;

            await Task.Delay(180, ct);
        }

        throw new TimeoutException($"Không tìm thấy '{cfg.DisplayName}' trên màn hình sau {config.WaitForMatchTimeoutMs / 1000.0:F1}s.");
    }

    private async Task<List<MatchResult>> WaitAndFindAllBlueBarsAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        CancellationToken ct)
    {
        var cfg = config.GetTarget(TargetKey.Step4BlueBar);
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, config.WaitForMatchTimeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);

            List<MatchResult> matches = config.BlueBarMode == BlueBarDetectMode.BlueColorBlob
                ? ScreenMatcher.FindBlueBarBlobs(screen, bounds.Location, cfg.SearchRoi)
                : ScreenMatcher.FindAllMatches(screen, bounds.Location, templates[TargetKey.Step4BlueBar], cfg, maxMatches: 64);

            if (matches.Count > 0)
            {
                // Small stabilization wait so all bars finish rendering if first frame just appeared
                await Task.Delay(120, ct);
                Rectangle bounds2 = ScreenMatcher.ResolveCaptureBounds(config);
                using var screen2 = ScreenMatcher.CaptureScreen(bounds2);
                var stableMatches = config.BlueBarMode == BlueBarDetectMode.BlueColorBlob
                    ? ScreenMatcher.FindBlueBarBlobs(screen2, bounds2.Location, cfg.SearchRoi)
                    : ScreenMatcher.FindAllMatches(screen2, bounds2.Location, templates[TargetKey.Step4BlueBar], cfg, maxMatches: 64);

                return stableMatches.Count >= matches.Count ? stableMatches : matches;
            }

            await Task.Delay(120, ct);
        }

        throw new TimeoutException($"Không tìm thấy thanh màu xanh lam nào sau {config.WaitForMatchTimeoutMs / 1000.0:F1}s.");
    }

    /// <summary>
    /// Checks if the "Kim cương ở đâu?" mini-game appeared after BATTLE.
    /// If found: clicks START, waits for shuffle, detects the spinning stump via motion tracking, clicks it.
    /// Returns true if the mini-game was detected and handled.
    /// </summary>
    private async Task<bool> TryHandleMiniGameAsync(
        AppConfig config,
        Dictionary<TargetKey, Bitmap> templates,
        int loopNum,
        CancellationToken ct)
    {
        if (!templates.ContainsKey(TargetKey.MiniGameStartButton))
            return false;

        var startCfg = config.GetTarget(TargetKey.MiniGameStartButton);
        var startTpl = templates[TargetKey.MiniGameStartButton];
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, config.MiniGameCheckTimeoutMs));

        // Quick check: is the START button visible?
        MatchResult? startMatch = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Rectangle bounds = ScreenMatcher.ResolveCaptureBounds(config);
            using var screen = ScreenMatcher.CaptureScreen(bounds);
            startMatch = ScreenMatcher.FindBestMatch(screen, bounds.Location, startTpl, startCfg);
            if (startMatch != null)
                break;
            await Task.Delay(120, ct);
        }

        if (startMatch == null)
            return false; // No mini-game appeared, continue normally

        _log($"[Mini-game] Phát hiện mini-game \"Kim cương ở đâu?\" (START: {startMatch.Score:P0})");
        UpdateStep($"Vòng #{loopNum} - Mini-game: Nhấn START");

        // Click the START button
        await HumanMouse.MoveAndClickAsync(startMatch.ScreenBounds, config, fastMode: false, ct);
        _log("[Mini-game] Đã nhấn START. Chờ xáo trộn...");

        // Wait for the shuffle animation to complete
        int shuffleWait = config.MiniGameShuffleWaitMs + Random.Shared.Next(0, 500);
        await Task.Delay(shuffleWait, ct);

        UpdateStep($"Vòng #{loopNum} - Mini-game: Tracking hộp quay...");

        // Estimate the stump search area: use the region above the START button, excluding
        // the text banner at the top. The stumps occupy roughly the middle 80% of the game panel.
        Rectangle captureBounds = ScreenMatcher.ResolveCaptureBounds(config);

        // Search ROI relative to capture bounds: exclude top ~18% (text) and bottom ~12% (START area)
        Rectangle motionSearchRoi = new(
            (int)(captureBounds.Width * 0.08),
            (int)(captureBounds.Height * 0.18),
            (int)(captureBounds.Width * 0.84),
            (int)(captureBounds.Height * 0.68));

        // Take multiple frame pairs and accumulate motion votes
        int samples = Math.Max(3, config.MiniGameMotionSamplesCount);
        int intervalMs = Math.Max(50, config.MiniGameMotionIntervalMs);
        int gridSize = Math.Clamp(config.MiniGameMotionGridSize, 8, 64);

        var votedPoints = new List<Point>();
        _log($"[Mini-game] Bắt đầu motion tracking ({samples} lần, mỗi {intervalMs}ms)...");

        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var frame1 = ScreenMatcher.CaptureScreen(captureBounds);
            await Task.Delay(intervalMs, ct);
            using var frame2 = ScreenMatcher.CaptureScreen(captureBounds);

            var motionPt = ScreenMatcher.FindMotionCenter(
                frame1, frame2, captureBounds.Location,
                motionSearchRoi, gridSize, perPixelThreshold: 28);

            if (motionPt.HasValue)
            {
                votedPoints.Add(motionPt.Value);
                _log($"   [Motion #{i + 1}] Phát hiện chuyển động tại ({motionPt.Value.X}, {motionPt.Value.Y})");
            }
            else
            {
                _log($"   [Motion #{i + 1}] Không phát hiện chuyển động đáng kể.");
            }

            // Small extra gap between samples so we get different animation frames
            if (i < samples - 1)
                await Task.Delay(Random.Shared.Next(30, 80), ct);
        }

        if (votedPoints.Count == 0)
        {
            _log("[Mini-game] Không tìm thấy hộp quay. Bỏ qua mini-game.");
            return true; // Still handled (START was clicked), just couldn't find the spinner
        }

        // Cluster the voted points and pick the densest cluster center.
        // Simple approach: average all points that are within a reasonable radius of the median.
        votedPoints.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        int medX = votedPoints[votedPoints.Count / 2].X;
        int medY = votedPoints[votedPoints.Count / 2].Y;

        double clusterRadius = gridSize * 2.5;
        var clusterPoints = votedPoints.Where(p =>
            Math.Sqrt((p.X - medX) * (p.X - medX) + (p.Y - medY) * (p.Y - medY)) <= clusterRadius
        ).ToList();

        if (clusterPoints.Count == 0)
            clusterPoints = votedPoints;

        int avgX = (int)Math.Round(clusterPoints.Average(p => p.X));
        int avgY = (int)Math.Round(clusterPoints.Average(p => p.Y));

        _log($"[Mini-game] Hộp quay trung bình: ({avgX}, {avgY}) từ {clusterPoints.Count}/{votedPoints.Count} điểm.");

        // Click on the spinning stump
        UpdateStep($"Vòng #{loopNum} - Mini-game: Nhấn hộp quay!");
        // Create a click target rect around the detected center
        int targetW = gridSize * 2;
        int targetH = gridSize * 2;
        var targetRect = new Rectangle(avgX - targetW / 2, avgY - targetH / 2, targetW, targetH);

        await HumanMouse.MoveAndClickAsync(targetRect, config, fastMode: false, ct);
        _log("[Mini-game] Đã nhấn hộp quay! Hoàn tất mini-game.");

        // Brief wait for the game to process the click and show result
        await Task.Delay(Random.Shared.Next(800, 1400), ct);

        return true;
    }

    private async Task<(bool FoundBoth, MatchResult? Claim, MatchResult? Dismantle, int ScansCount)>
        TrackCooldownLinesAsync(
            AppConfig config,
            Dictionary<TargetKey, Bitmap> templates,
            double trackDurationSeconds,
            CancellationToken ct)
    {
        var claimCfg = config.GetTarget(TargetKey.Step6ClaimButton);
        var dismantleCfg = config.GetTarget(TargetKey.Step6DismantleButton);
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

            var whiteCd = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6CooldownWhite], whiteCfg);
            var redCd = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6CooldownRed], redCfg);

            lastClaim = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6ClaimButton], claimCfg) ?? lastClaim;
            lastDismantle = ScreenMatcher.FindBestMatch(screen, bounds.Location, templates[TargetKey.Step6DismantleButton], dismantleCfg) ?? lastDismantle;

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
