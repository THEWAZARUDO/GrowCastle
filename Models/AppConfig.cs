using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrowCastleAuto.Models;

public enum TargetKey
{
    Step1DragonIcon,
    Step2LegendaryDragon,
    Step3BattleButton,
    MiniGameStartButton,
    Step4BlueBar,
    Step6CooldownWhite,
    Step6CooldownRed,
    Step6ClaimButton,
    Step6DismantleButton
}

public enum BlueBarClickOrder
{
    NaturalRandomized,
    LeftToRight,
    TopToBottom,
    FullyShuffled
}

public enum BlueBarDetectMode
{
    TemplateMatching,
    BlueColorBlob
}

public sealed class TargetConfig
{
    public TargetKey Key { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TemplateFileName { get; set; } = string.Empty;
    public double Threshold { get; set; } = 0.85;
    public bool RequireColorMatch { get; set; }

    /// <summary>
    /// Optional relative search region (0.0 to 1.0 of captured area). Null means search entire target area.
    /// </summary>
    public NormalizedRect? SearchRoi { get; set; }
}

public sealed class NormalizedRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Rectangle ToPixelRect(Rectangle bounds)
    {
        int x = bounds.X + (int)Math.Round(X * bounds.Width);
        int y = bounds.Y + (int)Math.Round(Y * bounds.Height);
        int w = Math.Max(1, (int)Math.Round(Width * bounds.Width));
        int h = Math.Max(1, (int)Math.Round(Height * bounds.Height));
        return Rectangle.Intersect(bounds, new Rectangle(x, y, w, h));
    }
}

public sealed class AppConfig
{
    public string TargetWindowTitle { get; set; } = string.Empty;
    public Rectangle? CustomCaptureRegion { get; set; }

    // Step delay: Math.Random() + 0.3 seconds (0.3s .. 1.3s)
    public double StepDelayMinSeconds { get; set; } = 0.30;
    public double StepDelayRandomSpanSeconds { get; set; } = 1.00;

    // Blue bar delay: ~50ms + random
    public int BlueBarDelayMinMs { get; set; } = 42;
    public int BlueBarDelayMaxMs { get; set; } = 72;

    // Post-blue-bar battle wait: ~3 seconds
    public double WaitAfterBlueBarsMinSeconds { get; set; } = 2.90;
    public double WaitAfterBlueBarsMaxSeconds { get; set; } = 3.20;

    // Continuous tracking window for both white & red Cooldown lines: 2 seconds
    public double LootTrackDurationSeconds { get; set; } = 2.00;

    // Post-loot wait before next loop: ~0.1 seconds
    public double WaitBeforeRepeatMinSeconds { get; set; } = 0.08;
    public double WaitBeforeRepeatMaxSeconds { get; set; } = 0.14;

    // Take a short break after a random number of completed loops
    public int LongPauseEveryMinLoops { get; set; } = 10;
    public int LongPauseEveryMaxLoops { get; set; } = 20;
    public double LongPauseDurationSeconds { get; set; } = 2.0;

    // Human mouse movement settings
    public int NormalMoveDurationMinMs { get; set; } = 170;
    public int NormalMoveDurationMaxMs { get; set; } = 390;
    public int FastMoveDurationMinMs { get; set; } = 22;
    public int FastMoveDurationMaxMs { get; set; } = 48;
    public double CurveIntensityMin { get; set; } = 0.10;
    public double CurveIntensityMax { get; set; } = 0.28;

    // Blue bar detection & clicking behavior
    public BlueBarDetectMode BlueBarMode { get; set; } = BlueBarDetectMode.TemplateMatching;
    public BlueBarClickOrder BlueBarOrder { get; set; } = BlueBarClickOrder.NaturalRandomized;
    public int WaitForMatchTimeoutMs { get; set; } = 12000;
    // No longer used: blue bars and the mini-game START button are now scanned in parallel right after BATTLE
    public int BlueBarAppearDelayMs { get; set; } = 450;

    // Reliability settings
    // Bring the game window to the foreground at the start of every loop
    public bool AutoFocusGameWindow { get; set; } = true;

    // After clicking BATTLE / NHẬN / PHÂN RÃ, check the button disappeared; if not, click again
    public bool VerifyClicks { get; set; } = true;
    public int ClickRetryMax { get; set; } = 3;
    public int ClickVerifyTimeoutMs { get; set; } = 700;

    // A timeout in one loop no longer stops the program; it restarts from step 1.
    // Stop only after this many failed loops in a row.
    public int MaxConsecutiveFailures { get; set; } = 5;

    // Mini-game "Kim cương ở đâu?" settings.
    // It is a shell game: the diamond drops into one stump, the 8 stumps tumble together and spread back
    // into the ring. Stumps look identical, so after the shuffle the program picks a stump from
    // MiniGameSlotMap (see below) or at random when the rule is unknown.
    public bool MiniGameEnabled { get; set; } = true;

    // Maximum time to wait for the shuffle to finish (stumps back in the ring and still)
    public int MiniGameTrackTimeoutMs { get; set; } = 12000;
    public int MiniGameSampleIntervalMs { get; set; } = 40;

    // A pixel counts as "changed" when the summed RGB difference reaches this value
    public int MiniGamePixelThreshold { get; set; } = 28;

    // A stump slot is "moving" in a frame pair when at least this fraction of its pixels changed
    public double MiniGameActiveRatio { get; set; } = 0.03;

    // Number of consecutive still frame pairs (all 8 stumps in place) that mean the shuffle is over
    public int MiniGameStableFrames { get; set; } = 6;

    // Fallback scale of the mini-game panel (788x594 = 1.0) when the diamond cannot be found
    public double MiniGameLayoutScale { get; set; } = 1.0;

    // Which stump to click, per stump the diamond dropped into. Index = stump the diamond dropped into,
    // value = stump to click, both 0..7 clockwise from the top stump (0 = top, 1 = top-right, 2 = right,
    // 3 = bottom-right, 4 = bottom, 5 = bottom-left, 6 = left, 7 = top-left). -1 = unknown (random click).
    // Follow the cup that spins independently (it holds the diamond) through the shuffle and click the slot
    // where it ends up. Needs OpenCV and the cup template; when unavailable the click falls back to
    // MiniGameSlotMap / random.
    public bool MiniGameUseSpinTracker { get; set; } = true;
    public string MiniGameCupTemplate { get; set; } = "cup_template.png";

    // Debug: save every captured mini-game frame to the "debug" folder next to the exe (for offline analysis)
    public bool MiniGameSaveFrames { get; set; } = false;

    public int[] MiniGameSlotMap { get; set; } = [-1, -1, -1, -1, -1, -1, -1, -1];

    public List<TargetConfig> Targets { get; set; } = CreateDefaultTargets();

    public TargetConfig GetTarget(TargetKey key)
    {
        var found = Targets.FirstOrDefault(t => t.Key == key);
        if (found != null) return found;
        var def = CreateDefaultTargets().First(t => t.Key == key);
        Targets.Add(def);
        return def;
    }

    public static List<TargetConfig> CreateDefaultTargets() =>
    [
        new()
        {
            Key = TargetKey.Step1DragonIcon,
            DisplayName = "1. Icon Tượng Rồng (Bước 1)",
            Description = "Icon mở danh sách rồng trên bản đồ",
            TemplateFileName = "step1_dragon_icon.png",
            Threshold = 0.84
        },
        new()
        {
            Key = TargetKey.Step2LegendaryDragon,
            DisplayName = "2. Rồng huyền thoại",
            Description = "Nút chọn Rồng huyền thoại",
            TemplateFileName = "step2_legendary_dragon.png",
            Threshold = 0.84
        },
        new()
        {
            Key = TargetKey.Step3BattleButton,
            DisplayName = "3. Nút BATTLE",
            Description = "Nút BATTLE vào trận",
            TemplateFileName = "step3_battle.png",
            Threshold = 0.84
        },
        new()
        {
            Key = TargetKey.MiniGameStartButton,
            DisplayName = "★ Mini-game START",
            Description = "Nút START của mini-game \"Kim cương ở đâu?\"",
            TemplateFileName = "minigame_start.png",
            Threshold = 0.82
        },
        new()
        {
            Key = TargetKey.Step4BlueBar,
            DisplayName = "4. Thanh màu xanh lam",
            Description = "Một thanh màu xanh lam mẫu (sẽ tìm và nhấn tất cả thanh)",
            TemplateFileName = "step4_blue_bar.png",
            Threshold = 0.82,
            RequireColorMatch = true
        },
        new()
        {
            Key = TargetKey.Step6CooldownWhite,
            DisplayName = "5. Chữ \"Giảm hồi chiêu\" (Trắng)",
            Description = "Dòng chỉ số Giảm hồi chiêu màu trắng",
            TemplateFileName = "step6_cooldown_white.png",
            Threshold = 0.85,
            RequireColorMatch = true
        },
        new()
        {
            Key = TargetKey.Step6CooldownRed,
            DisplayName = "6. Chữ \"Giảm hồi chiêu\" (Đỏ)",
            Description = "Dòng chỉ số Giảm hồi chiêu màu đỏ",
            TemplateFileName = "step6_cooldown_red.png",
            Threshold = 0.85,
            RequireColorMatch = true
        },
        new()
        {
            Key = TargetKey.Step6ClaimButton,
            DisplayName = "7. Nút NHẬN",
            Description = "Nhấn khi có CẢ 2 dòng Giảm hồi chiêu (trắng & đỏ)",
            TemplateFileName = "step6_claim.png",
            Threshold = 0.84
        },
        new()
        {
            Key = TargetKey.Step6DismantleButton,
            DisplayName = "8. Nút PHÂN RÃ",
            Description = "Nhấn khi KHÔNG có đủ cả 2 dòng Giảm hồi chiêu",
            TemplateFileName = "step6_dismantle.png",
            Threshold = 0.84
        }
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            // Ensure all default targets exist
            foreach (var def in CreateDefaultTargets())
            {
                if (cfg.Targets.All(t => t.Key != def.Key))
                    cfg.Targets.Add(def);
            }
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
