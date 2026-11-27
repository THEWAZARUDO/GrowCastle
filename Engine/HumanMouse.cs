using System.Diagnostics;
using GrowCastleAuto.Models;
using GrowCastleAuto.Native;

namespace GrowCastleAuto.Engine;

public static class HumanMouse
{
    public static Point PickHumanPointInRect(Rectangle rect, double innerMarginRatio = 0.22)
    {
        int marginX = Math.Max(1, (int)(rect.Width * innerMarginRatio));
        int marginY = Math.Max(1, (int)(rect.Height * innerMarginRatio));

        int minX = rect.Left + Math.Min(marginX, rect.Width / 3);
        int maxX = rect.Right - Math.Min(marginX, rect.Width / 3);
        int minY = rect.Top + Math.Min(marginY, rect.Height / 3);
        int maxY = rect.Bottom - Math.Min(marginY, rect.Height / 3);

        if (maxX <= minX) { minX = rect.Left; maxX = rect.Right; }
        if (maxY <= minY) { minY = rect.Top; maxY = rect.Bottom; }

        // Average two uniforms (triangular/pseudo-Gaussian distribution biased slightly toward center)
        double rx = (Random.Shared.NextDouble() + Random.Shared.NextDouble()) * 0.5;
        double ry = (Random.Shared.NextDouble() + Random.Shared.NextDouble()) * 0.5;

        int x = (int)Math.Round(minX + rx * Math.Max(0, maxX - minX));
        int y = (int)Math.Round(minY + ry * Math.Max(0, maxY - minY));
        return new Point(x, y);
    }

    public static async Task MoveAndClickAsync(
        Rectangle targetRect,
        AppConfig config,
        bool fastMode,
        CancellationToken ct)
    {
        Point target = PickHumanPointInRect(targetRect, fastMode ? 0.25 : 0.20);
        await MoveToAsync(target, config, fastMode, ct);
        await ClickAsync(fastMode, ct);
    }

    public static async Task MoveToAsync(
        Point target,
        AppConfig config,
        bool fastMode,
        CancellationToken ct)
    {
        Point start = Win32.GetMousePosition();
        double dx = target.X - start.X;
        double dy = target.Y - start.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < 2.0)
        {
            Win32.MoveMouseAbsolute(target.X, target.Y);
            return;
        }

        // Calculate randomized duration based on distance + random variance
        int minDur = fastMode ? config.FastMoveDurationMinMs : config.NormalMoveDurationMinMs;
        int maxDur = fastMode ? config.FastMoveDurationMaxMs : config.NormalMoveDurationMaxMs;
        if (maxDur < minDur) maxDur = minDur;

        double distFactor = Math.Clamp(dist / (fastMode ? 450.0 : 900.0), 0.35, 1.35);
        double baseDur = minDur + Random.Shared.NextDouble() * (maxDur - minDur);
        int durationMs = Math.Max(15, (int)Math.Round(baseDur * (0.65 + 0.35 * distFactor)));

        // Unit vectors along path and perpendicular
        double ux = dx / dist;
        double uy = dy / dist;
        double px = -uy;
        double py = ux;

        // Randomize curve intensity, direction, and shape (C-curve vs subtle S-curve)
        double intensityMin = Math.Clamp(config.CurveIntensityMin, 0.02, 0.5);
        double intensityMax = Math.Clamp(config.CurveIntensityMax, intensityMin, 0.6);
        double intensity1 = intensityMin + Random.Shared.NextDouble() * (intensityMax - intensityMin);
        double intensity2 = intensityMin + Random.Shared.NextDouble() * (intensityMax - intensityMin);

        double sign1 = Random.Shared.Next(2) == 0 ? 1.0 : -1.0;
        // 30% chance of an S-curve where second control point bends mildly the other way
        double sign2 = Random.Shared.NextDouble() < 0.30 ? -sign1 * 0.55 : sign1;

        double arcCap = fastMode ? 45.0 : 140.0;
        double offset1 = Math.Min(arcCap, dist * intensity1) * sign1;
        double offset2 = Math.Min(arcCap, dist * intensity2) * sign2;

        double t1 = 0.20 + Random.Shared.NextDouble() * 0.22; // 0.20..0.42
        double t2 = 0.58 + Random.Shared.NextDouble() * 0.26; // 0.58..0.84

        double p1x = start.X + dx * t1 + px * offset1;
        double p1y = start.Y + dy * t1 + py * offset1;
        double p2x = start.X + dx * t2 + px * offset2;
        double p2y = start.Y + dy * t2 + py * offset2;

        // Subtle organic tremor wave parameters unique to this move
        double wobbleFreq = 2.0 + Random.Shared.NextDouble() * 3.0;
        double wobblePhase = Random.Shared.NextDouble() * Math.PI * 2.0;
        double wobbleAmp = fastMode ? 1.2 : Math.Min(4.0, dist * 0.012);

        // Randomized ease-in-out exponent so velocity profile varies each time
        double easePower = 1.7 + Random.Shared.NextDouble() * 0.9;

        int steps = Math.Clamp(durationMs / (fastMode ? 6 : 8), fastMode ? 4 : 12, 85);
        var sw = Stopwatch.StartNew();

        Win32.timeBeginPeriod(1);
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();

                double linearT = (double)i / steps;
                double easedT = EaseInOut(linearT, easePower);

                // Cubic Bezier evaluation
                double u = 1.0 - easedT;
                double b0 = u * u * u;
                double b1 = 3.0 * u * u * easedT;
                double b2 = 3.0 * u * easedT * easedT;
                double b3 = easedT * easedT * easedT;

                double x = b0 * start.X + b1 * p1x + b2 * p2x + b3 * target.X;
                double y = b0 * start.Y + b1 * p1y + b2 * p2y + b3 * target.Y;

                // Envelope fades smoothly to 0 at start & end so we land accurately on target
                double envelope = Math.Sin(linearT * Math.PI);
                double tremor = Math.Sin(linearT * Math.PI * wobbleFreq + wobblePhase) * wobbleAmp * envelope;
                x += px * tremor;
                y += py * tremor;

                Win32.MoveMouseAbsolute((int)Math.Round(x), (int)Math.Round(y));

                double targetElapsedMs = linearT * durationMs;
                while (sw.Elapsed.TotalMilliseconds < targetElapsedMs)
                {
                    ct.ThrowIfCancellationRequested();
                    double remaining = targetElapsedMs - sw.Elapsed.TotalMilliseconds;
                    if (remaining > 2.0)
                        await Task.Delay(1, ct);
                    else
                        Thread.SpinWait(50);
                }
            }

            // Final exact landing
            Win32.MoveMouseAbsolute(target.X, target.Y);
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    public static async Task ClickAsync(bool fastMode, CancellationToken ct)
    {
        int holdMs = fastMode
            ? Random.Shared.Next(14, 28)
            : Random.Shared.Next(26, 62);

        Win32.timeBeginPeriod(1);
        try
        {
            Win32.LeftDown();
            try
            {
                await Task.Delay(holdMs, ct);
            }
            finally
            {
                // Always release, even if the run was cancelled while the button was held down
                Win32.LeftUp();
            }
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    private static double EaseInOut(double t, double power)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        double tp = Math.Pow(t, power);
        double omp = Math.Pow(1.0 - t, power);
        return tp / (tp + omp);
    }
}
