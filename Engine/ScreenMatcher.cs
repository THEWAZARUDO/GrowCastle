using System.Drawing.Imaging;
using GrowCastleAuto.Models;
using GrowCastleAuto.Native;

namespace GrowCastleAuto.Engine;

public sealed record MatchResult(Rectangle ScreenBounds, double Score);

public static class ScreenMatcher
{
    public static Rectangle ResolveCaptureBounds(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.TargetWindowTitle))
        {
            var winBounds = Win32.FindWindowBoundsByTitle(config.TargetWindowTitle);
            if (winBounds.HasValue && winBounds.Value.Width > 0 && winBounds.Value.Height > 0)
            {
                return winBounds.Value;
            }
        }

        if (config.CustomCaptureRegion.HasValue &&
            config.CustomCaptureRegion.Value.Width > 10 &&
            config.CustomCaptureRegion.Value.Height > 10)
        {
            return config.CustomCaptureRegion.Value;
        }

        return Win32.GetVirtualScreenBounds();
    }

    public static Bitmap CaptureScreen(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static MatchResult? FindBestMatch(
        Bitmap haystack,
        Point haystackOrigin,
        Bitmap template,
        TargetConfig cfg)
    {
        var matches = FindMatchesInternal(haystack, haystackOrigin, template, cfg, findAll: false, maxMatches: 1);
        return matches.Count > 0 ? matches[0] : null;
    }

    public static List<MatchResult> FindAllMatches(
        Bitmap haystack,
        Point haystackOrigin,
        Bitmap template,
        TargetConfig cfg,
        int maxMatches = 64)
    {
        return FindMatchesInternal(haystack, haystackOrigin, template, cfg, findAll: true, maxMatches: maxMatches);
    }

    private static unsafe List<MatchResult> FindMatchesInternal(
        Bitmap haystack,
        Point haystackOrigin,
        Bitmap template,
        TargetConfig cfg,
        bool findAll,
        int maxMatches)
    {
        var results = new List<MatchResult>();
        int hw = haystack.Width;
        int hh = haystack.Height;
        int tw = template.Width;
        int th = template.Height;

        if (tw <= 0 || th <= 0 || tw > hw || th > hh)
            return results;

        Rectangle searchRect = cfg.SearchRoi != null
            ? cfg.SearchRoi.ToPixelRect(new Rectangle(0, 0, hw, hh))
            : new Rectangle(0, 0, hw, hh);

        int maxX = searchRect.Right - tw;
        int maxY = searchRect.Bottom - th;
        if (maxX < searchRect.Left || maxY < searchRect.Top)
            return results;

        using var hay24 = Ensure24bpp(haystack);
        using var tpl24 = Ensure24bpp(template);

        var hayData = hay24.LockBits(new Rectangle(0, 0, hw, hh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var tplData = tpl24.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            byte* hayPtr = (byte*)hayData.Scan0;
            byte* tplPtr = (byte*)tplData.Scan0;
            int hayStride = hayData.Stride;
            int tplStride = tplData.Stride;

            // Analyze template foreground pixels if RequireColorMatch is enabled
            bool[]? fgMask = null;
            int fgCount = 0;
            if (cfg.RequireColorMatch)
            {
                fgMask = BuildForegroundMask(tplPtr, tplStride, tw, th, out fgCount);
            }

            // Build coarse sample points (stride across template, favoring foreground points too)
            var coarseSamples = BuildCoarseSamples(tplPtr, tplStride, tw, th, fgMask, maxSamples: 40);

            double threshold = Math.Clamp(cfg.Threshold, 0.40, 0.99);
            // Coarse filter is slightly looser than final threshold to avoid missing off-by-1 candidates
            double coarseMin = Math.Max(0.35, threshold - 0.14);

            var candidates = new List<(int x, int y, double score)>();

            // Stage 1: Coarse scan at stride 2
            int step = 2;
            for (int y = searchRect.Top; y <= maxY; y += step)
            {
                for (int x = searchRect.Left; x <= maxX; x += step)
                {
                    double coarseScore = ComputeCoarseScore(hayPtr, hayStride, x, y, coarseSamples);
                    if (coarseScore >= coarseMin)
                    {
                        candidates.Add((x, y, coarseScore));
                    }
                }
            }

            if (candidates.Count == 0)
                return results;

            // Evaluate refined neighborhood (+-1 px) around coarse hits
            var exactHits = new List<MatchResult>();
            var visited = new HashSet<long>();

            // If only looking for single best, sort coarse candidates descending first for fast pruning
            if (!findAll)
            {
                candidates.Sort((a, b) => b.score.CompareTo(a.score));
            }

            double bestScoreSoFar = 0;
            int candidatesChecked = 0;

            foreach (var c in candidates)
            {
                if (!findAll && candidatesChecked > 300 && bestScoreSoFar >= threshold)
                    break;

                candidatesChecked++;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ry = c.y + dy;
                    if (ry < searchRect.Top || ry > maxY) continue;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int rx = c.x + dx;
                        if (rx < searchRect.Left || rx > maxX) continue;

                        long key = ((long)ry << 20) | (uint)rx;
                        if (!visited.Add(key)) continue;

                        double score = ComputeExactScore(
                            hayPtr, hayStride,
                            tplPtr, tplStride,
                            rx, ry, tw, th,
                            fgMask, fgCount);

                        if (score >= threshold)
                        {
                            var screenRect = new Rectangle(haystackOrigin.X + rx, haystackOrigin.Y + ry, tw, th);
                            exactHits.Add(new MatchResult(screenRect, score));
                            if (score > bestScoreSoFar)
                                bestScoreSoFar = score;
                        }
                    }
                }
            }

            if (exactHits.Count == 0)
                return results;

            // Sort descending by score and apply Non-Maximum Suppression
            exactHits.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (!findAll)
            {
                results.Add(exactHits[0]);
                return results;
            }

            foreach (var hit in exactHits)
            {
                bool overlaps = false;
                foreach (var kept in results)
                {
                    if (IoU(hit.ScreenBounds, kept.ScreenBounds) > 0.25 ||
                        CentersTooClose(hit.ScreenBounds, kept.ScreenBounds, tw * 0.55, th * 0.55))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    results.Add(hit);
                    if (results.Count >= maxMatches)
                        break;
                    }
            }

            return results;
        }
        finally
        {
            hay24.UnlockBits(hayData);
            tpl24.UnlockBits(tplData);
        }
    }

    /// <summary>
    /// Alternative blue-bar detector that locates horizontal light-blue bars by color clustering.
    /// Useful when bars differ slightly in fill length or scale.
    /// </summary>
    public static unsafe List<MatchResult> FindBlueBarBlobs(
        Bitmap haystack,
        Point haystackOrigin,
        NormalizedRect? roi = null)
    {
        int hw = haystack.Width;
        int hh = haystack.Height;
        Rectangle searchRect = roi != null
            ? roi.ToPixelRect(new Rectangle(0, 0, hw, hh))
            : new Rectangle(0, 0, hw, hh);

        using var hay24 = Ensure24bpp(haystack);
        var data = hay24.LockBits(new Rectangle(0, 0, hw, hh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            byte* ptr = (byte*)data.Scan0;
            int stride = data.Stride;

            int rw = searchRect.Width;
            int rh = searchRect.Height;
            bool[] mask = new bool[rw * rh];

            for (int y = 0; y < rh; y++)
            {
                byte* row = ptr + (searchRect.Top + y) * stride + searchRect.Left * 3;
                int rowOffset = y * rw;
                for (int x = 0; x < rw; x++)
                {
                    byte b = row[x * 3];
                    byte g = row[x * 3 + 1];
                    byte r = row[x * 3 + 2];

                    // Light-blue / cyan bar color: B high, G medium-high, R noticeably lower than B
                    if (b >= 175 && g >= 115 && r <= 125 && (b - r) >= 75)
                    {
                        mask[rowOffset + x] = true;
                    }
                }
            }

            // Connected components (4-connected BFS)
            bool[] visited = new bool[rw * rh];
            var queue = new Queue<int>();
            var blobs = new List<MatchResult>();

            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    int idx = y * rw + x;
                    if (!mask[idx] || visited[idx]) continue;

                    int minX = x, maxX = x, minY = y, maxY = y;
                    int count = 0;
                    visited[idx] = true;
                    queue.Enqueue(idx);

                    while (queue.Count > 0)
                    {
                        int cur = queue.Dequeue();
                        int cx = cur % rw;
                        int cy = cur / rw;
                        count++;

                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;

                        if (cx > 0 && mask[cur - 1] && !visited[cur - 1]) { visited[cur - 1] = true; queue.Enqueue(cur - 1); }
                        if (cx + 1 < rw && mask[cur + 1] && !visited[cur + 1]) { visited[cur + 1] = true; queue.Enqueue(cur + 1); }
                        if (cy > 0 && mask[cur - rw] && !visited[cur - rw]) { visited[cur - rw] = true; queue.Enqueue(cur - rw); }
                        if (cy + 1 < rh && mask[cur + rw] && !visited[cur + rw]) { visited[cur + rw] = true; queue.Enqueue(cur + rw); }
                    }

                    int bw = maxX - minX + 1;
                    int bh = maxY - minY + 1;
                    // Filter to horizontal bar shapes
                    if (bw >= 16 && bw <= 220 && bh >= 3 && bh <= 35 && bw >= bh * 1.8 && count >= 35)
                    {
                        var rect = new Rectangle(
                            haystackOrigin.X + searchRect.Left + minX,
                            haystackOrigin.Y + searchRect.Top + minY,
                            bw,
                            bh);
                        blobs.Add(new MatchResult(rect, 1.0));
                    }
                }
            }

            return blobs;
        }
        finally
        {
            hay24.UnlockBits(data);
        }
    }

    /// <summary>
    /// Detects the center of the region with the most motion (pixel changes) between two frames.
    /// Used to find the spinning stump in the mini-game.
    /// Returns the center point (in screen coordinates) of the grid cell with the highest motion,
    /// or null if no significant motion is detected.
    /// </summary>
    public static unsafe Point? FindMotionCenter(
        Bitmap frame1,
        Bitmap frame2,
        Point screenOrigin,
        Rectangle? relativeSearchRoi,
        int gridSize = 24,
        int perPixelThreshold = 28)
    {
        int fw = frame1.Width;
        int fh = frame1.Height;
        if (fw != frame2.Width || fh != frame2.Height)
            return null;

        Rectangle searchRect = relativeSearchRoi ?? new Rectangle(0, 0, fw, fh);
        searchRect = Rectangle.Intersect(searchRect, new Rectangle(0, 0, fw, fh));
        if (searchRect.Width < gridSize || searchRect.Height < gridSize)
            return null;

        using var f1_24 = Ensure24bpp(frame1);
        using var f2_24 = Ensure24bpp(frame2);

        var d1 = f1_24.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var d2 = f2_24.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            byte* p1 = (byte*)d1.Scan0;
            byte* p2 = (byte*)d2.Scan0;
            int stride1 = d1.Stride;
            int stride2 = d2.Stride;

            int gridCols = (searchRect.Width + gridSize - 1) / gridSize;
            int gridRows = (searchRect.Height + gridSize - 1) / gridSize;
            long[] motionGrid = new long[gridRows * gridCols];

            for (int y = searchRect.Top; y < searchRect.Bottom; y++)
            {
                byte* row1 = p1 + y * stride1;
                byte* row2 = p2 + y * stride2;
                int gridRow = (y - searchRect.Top) / gridSize;

                for (int x = searchRect.Left; x < searchRect.Right; x++)
                {
                    int i3 = x * 3;
                    int diff = Math.Abs(row1[i3] - row2[i3]) +
                               Math.Abs(row1[i3 + 1] - row2[i3 + 1]) +
                               Math.Abs(row1[i3 + 2] - row2[i3 + 2]);

                    // Only count pixels that actually changed (filter noise)
                    if (diff >= perPixelThreshold)
                    {
                        int gridCol = (x - searchRect.Left) / gridSize;
                        motionGrid[gridRow * gridCols + gridCol] += diff;
                    }
                }
            }

            // Find the grid cell with maximum accumulated motion
            int bestCell = -1;
            long bestMotion = 0;
            for (int i = 0; i < motionGrid.Length; i++)
            {
                if (motionGrid[i] > bestMotion)
                {
                    bestMotion = motionGrid[i];
                    bestCell = i;
                }
            }

            if (bestCell < 0 || bestMotion < gridSize * gridSize * perPixelThreshold / 2)
                return null;

            // Compute weighted average of the best cell and its neighbors for sub-grid accuracy
            int bestRow = bestCell / gridCols;
            int bestCol = bestCell % gridCols;

            double weightedX = 0, weightedY = 0, totalWeight = 0;
            for (int dr = -1; dr <= 1; dr++)
            {
                int r = bestRow + dr;
                if (r < 0 || r >= gridRows) continue;
                for (int dc = -1; dc <= 1; dc++)
                {
                    int c = bestCol + dc;
                    if (c < 0 || c >= gridCols) continue;
                    long w = motionGrid[r * gridCols + c];
                    if (w <= 0) continue;

                    double cellCenterX = searchRect.Left + (c + 0.5) * gridSize;
                    double cellCenterY = searchRect.Top + (r + 0.5) * gridSize;
                    weightedX += cellCenterX * w;
                    weightedY += cellCenterY * w;
                    totalWeight += w;
                }
            }

            if (totalWeight <= 0) return null;

            int cx = screenOrigin.X + (int)Math.Round(weightedX / totalWeight);
            int cy = screenOrigin.Y + (int)Math.Round(weightedY / totalWeight);
            return new Point(cx, cy);
        }
        finally
        {
            f1_24.UnlockBits(d1);
            f2_24.UnlockBits(d2);
        }
    }

    /// <summary>
    /// Measures, for every region of interest, the fraction of pixels (0..1) that changed
    /// noticeably between two frames of the same size. ROIs are relative to the frames.
    /// Used by the mini-game to tell which of the 8 stumps is moving.
    /// </summary>
    public static unsafe double[] MeasureMotionRatios(
        Bitmap frame1,
        Bitmap frame2,
        IReadOnlyList<Rectangle> rois,
        int perPixelThreshold = 28)
    {
        var result = new double[rois.Count];
        int fw = frame1.Width;
        int fh = frame1.Height;
        if (fw != frame2.Width || fh != frame2.Height)
            return result;

        using var f1_24 = Ensure24bpp(frame1);
        using var f2_24 = Ensure24bpp(frame2);

        var d1 = f1_24.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var d2 = f2_24.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            byte* p1 = (byte*)d1.Scan0;
            byte* p2 = (byte*)d2.Scan0;

            for (int i = 0; i < rois.Count; i++)
            {
                Rectangle r = Rectangle.Intersect(rois[i], new Rectangle(0, 0, fw, fh));
                if (r.Width <= 0 || r.Height <= 0)
                    continue;

                long changed = 0;
                for (int y = r.Top; y < r.Bottom; y++)
                {
                    byte* row1 = p1 + y * d1.Stride;
                    byte* row2 = p2 + y * d2.Stride;
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        int i3 = x * 3;
                        int diff = Math.Abs(row1[i3] - row2[i3]) +
                                   Math.Abs(row1[i3 + 1] - row2[i3 + 1]) +
                                   Math.Abs(row1[i3 + 2] - row2[i3 + 2]);
                        if (diff >= perPixelThreshold)
                            changed++;
                    }
                }

                result[i] = (double)changed / ((long)r.Width * r.Height);
            }
        }
        finally
        {
            f1_24.UnlockBits(d1);
            f2_24.UnlockBits(d2);
        }

        return result;
    }

    private static Bitmap Ensure24bpp(Bitmap src)
    {
        if (src.PixelFormat == PixelFormat.Format24bppRgb)
            return (Bitmap)src.Clone();

        var copy = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        return copy;
    }

    private static unsafe bool[] BuildForegroundMask(byte* tplPtr, int tplStride, int tw, int th, out int fgCount)
    {
        // Estimate background color from outer border pixels
        long sumB = 0, sumG = 0, sumR = 0;
        int borderCount = 0;

        for (int y = 0; y < th; y++)
        {
            byte* row = tplPtr + y * tplStride;
            for (int x = 0; x < tw; x++)
            {
                if (y == 0 || y == th - 1 || x == 0 || x == tw - 1)
                {
                    sumB += row[x * 3];
                    sumG += row[x * 3 + 1];
                    sumR += row[x * 3 + 2];
                    borderCount++;
                }
            }
        }

        int bgB = borderCount > 0 ? (int)(sumB / borderCount) : 30;
        int bgG = borderCount > 0 ? (int)(sumG / borderCount) : 30;
        int bgR = borderCount > 0 ? (int)(sumR / borderCount) : 30;

        bool[] mask = new bool[tw * th];
        fgCount = 0;

        for (int y = 0; y < th; y++)
        {
            byte* row = tplPtr + y * tplStride;
            for (int x = 0; x < tw; x++)
            {
                int b = row[x * 3];
                int g = row[x * 3 + 1];
                int r = row[x * 3 + 2];
                int diff = Math.Abs(b - bgB) + Math.Abs(g - bgG) + Math.Abs(r - bgR);
                if (diff > 95)
                {
                    mask[y * tw + x] = true;
                    fgCount++;
                }
            }
        }

        return mask;
    }

    private readonly record struct SamplePoint(int X, int Y, byte B, byte G, byte R);

    private static unsafe List<SamplePoint> BuildCoarseSamples(
        byte* tplPtr,
        int tplStride,
        int tw,
        int th,
        bool[]? fgMask,
        int maxSamples)
    {
        var samples = new List<SamplePoint>(maxSamples);
        int stepX = Math.Max(1, tw / 7);
        int stepY = Math.Max(1, th / 5);

        for (int y = stepY / 2; y < th; y += stepY)
        {
            byte* row = tplPtr + y * tplStride;
            for (int x = stepX / 2; x < tw; x += stepX)
            {
                samples.Add(new SamplePoint(x, y, row[x * 3], row[x * 3 + 1], row[x * 3 + 2]));
                if (samples.Count >= maxSamples - 10)
                    break;
            }
            if (samples.Count >= maxSamples - 10)
                break;
        }

        // Also add up to 10 explicit foreground samples if available
        if (fgMask != null)
        {
            int added = 0;
            int stride = Math.Max(1, (tw * th) / 40);
            for (int idx = 0; idx < fgMask.Length && added < 10 && samples.Count < maxSamples; idx += stride)
            {
                if (!fgMask[idx]) continue;
                int x = idx % tw;
                int y = idx / tw;
                byte* p = tplPtr + y * tplStride + x * 3;
                samples.Add(new SamplePoint(x, y, p[0], p[1], p[2]));
                added++;
            }
        }

        return samples;
    }

    private static unsafe double ComputeCoarseScore(
        byte* hayPtr,
        int hayStride,
        int ox,
        int oy,
        List<SamplePoint> samples)
    {
        long errSum = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            byte* p = hayPtr + (oy + s.Y) * hayStride + (ox + s.X) * 3;
            errSum += Math.Abs(p[0] - s.B) + Math.Abs(p[1] - s.G) + Math.Abs(p[2] - s.R);
        }

        double maxErr = samples.Count * 3.0 * 255.0;
        return 1.0 - (errSum / maxErr);
    }

    private static unsafe double ComputeExactScore(
        byte* hayPtr,
        int hayStride,
        byte* tplPtr,
        int tplStride,
        int ox,
        int oy,
        int tw,
        int th,
        bool[]? fgMask,
        int fgCount)
    {
        long totalErr = 0;
        long fgErr = 0;

        for (int y = 0; y < th; y++)
        {
            byte* hRow = hayPtr + (oy + y) * hayStride + ox * 3;
            byte* tRow = tplPtr + y * tplStride;

            for (int x = 0; x < tw; x++)
            {
                int i3 = x * 3;
                int err = Math.Abs(hRow[i3] - tRow[i3]) +
                          Math.Abs(hRow[i3 + 1] - tRow[i3 + 1]) +
                          Math.Abs(hRow[i3 + 2] - tRow[i3 + 2]);

                totalErr += err;
                if (fgMask != null && fgMask[y * tw + x])
                {
                    fgErr += err;
                }
            }
        }

        double overallScore = 1.0 - (totalErr / (tw * th * 3.0 * 255.0));
        if (fgMask == null || fgCount < 10)
            return overallScore;

        double fgScore = 1.0 - (fgErr / (fgCount * 3.0 * 255.0));
        return 0.40 * overallScore + 0.60 * fgScore;
    }

    private static double IoU(Rectangle a, Rectangle b)
    {
        var inter = Rectangle.Intersect(a, b);
        if (inter.IsEmpty) return 0;
        double interArea = (double)inter.Width * inter.Height;
        double unionArea = (double)a.Width * a.Height + (double)b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0;
    }

    private static bool CentersTooClose(Rectangle a, Rectangle b, double minDx, double minDy)
    {
        double cxA = a.Left + a.Width * 0.5;
        double cyA = a.Top + a.Height * 0.5;
        double cxB = b.Left + b.Width * 0.5;
        double cyB = b.Top + b.Height * 0.5;
        return Math.Abs(cxA - cxB) < minDx && Math.Abs(cyA - cyB) < minDy;
    }
}
