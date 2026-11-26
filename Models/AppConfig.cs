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
    public int BlueBarAppearDelayMs { get; set; } = 450;

    // Mini-game "Kim cương ở đâu?" settings
    public bool MiniGameEnabled { get; set; } = true;
    public int MiniGameCheckTimeoutMs { get; set; } = 1800;

    // Short wait after pressing START before tracking begins (+ up to 500ms random).
    // The correct stump spins from the start, so there is no need to wait for the shuffle to end.
    public int MiniGameShuffleWaitMs { get; set; } = 600;

    // Keep tracking the 8 stumps (retrying) for at most this long after the initial wait
    public int MiniGameTrackTimeoutMs { get; set; } = 12000;
    public int MiniGameSampleIntervalMs { get; set; } = 70;

    // A pixel counts as "changed" when the summed RGB difference reaches this value
    public int MiniGamePixelThreshold { get; set; } = 28;

    // A stump is "moving" in a frame pair when at least this fraction of its pixels changed
    public double MiniGameActiveRatio { get; set; } = 0.05;

    // A stump is a candidate once it was the ONLY moving one in at least this many of the last
    // (this + 2) frame pairs. Because the spinner swaps slots every few seconds, only recent frames count.
    public int MiniGameStableFrames { get; set; } = 4;

    // Scale of the mini-game panel relative to the reference layout (788x594). 1.0 = same size.
    public double MiniGameLayoutScale { get; set; } = 1.0;

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
