using System.Drawing;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace GrowCastleAuto.Engine;

/// <summary>
/// Follows the one cup that holds the diamond in the "Kim cương ở đâu?" mini-game.
///
/// How the mini-game animates (measured on a screen recording): during the shuffle ALL cups share one
/// common rotation angle that grows steadily every frame, EXCEPT the cup with the diamond, which spins
/// independently. So per frame we
///   1. find the common angle (the angle at which most cups match the cup template),
///   2. "explain away" every cup that matches the template at that angle,
///   3. what is left of the cup-coloured pixels is the diamond cup (the residual blob),
///   4. follow that blob with a gated, velocity-predicted track.
/// When all 8 cups match the common angle again the diamond cup has stopped spinning differently; from
/// then on it is followed as the nearest of the 8 aligned cups until it lands in its slot.
///
/// This is a 1:1 port of the Python reference implementation (spin_track2.py), which tracked the diamond
/// cup correctly through the whole shuffle of the recorded video and landed it in the right slot.
/// </summary>
public sealed class CupSpinTracker : IDisposable
{
    public enum TrackPhase { Idle, Spinning, Aligned }

    private const int AngleStep = 15;
    private const int AngleWindow = 90;       // search +-90 degrees around the previous common angle

    private readonly Dictionary<int, (Mat Gray, Mat Mask)> _rot = new();
    private readonly Mat _kernel3;
    private readonly Mat _kernel5;
    private readonly double _k;               // working resolution factor
    private readonly double _cupArea;         // cup-coloured area of one cup at working resolution
    private readonly int _nms;
    private readonly int _ox;
    private readonly int _oy;

    private int? _theta;
    private double _px;
    private double _py;
    private double _vx;
    private double _vy;
    private int _alignedStreak;

    public TrackPhase Phase { get; private set; } = TrackPhase.Idle;
    public double X => _px;
    public double Y => _py;
    public double CupWidth { get; }
    public int Frames { get; private set; }
    public int Misses { get; private set; }
    public int TotalMisses { get; private set; }
    public int CommonAngle => _theta ?? 0;

    private CupSpinTracker(Mat templateBgra, double cupTanWidth, double cupTanArea, int ringOriginX, int ringOriginY)
    {
        _ox = ringOriginX;
        _oy = ringOriginY;

        // Scale the template so its cup has the same size as the cups on screen
        double tplTanW;
        using (var bgr = new Mat())
        {
            Cv2.CvtColor(templateBgra, bgr, ColorConversionCodes.BGRA2BGR);
            using var tm = TanMask(bgr);
            tplTanW = Math.Max(1, Cv2.BoundingRect(tm).Width);
        }

        double s = cupTanWidth / tplTanW;
        CupWidth = templateBgra.Cols * s;
        double cupH = templateBgra.Rows * s;
        _k = Math.Clamp(45.0 / cupH, 0.2, 1.0);

        int tw = Math.Max(8, (int)Math.Round(CupWidth * _k));
        int th = Math.Max(8, (int)Math.Round(cupH * _k));
        _cupArea = cupTanArea * _k * _k;
        _nms = Math.Max(6, (int)Math.Round(0.17 * CupWidth * _k * 2));

        using var scaled = new Mat();
        Cv2.Resize(templateBgra, scaled, new OpenCvSharp.Size(tw, th), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(scaled, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Split(scaled, out Mat[] channels);
        using var mask0 = new Mat();
        Cv2.Threshold(channels[3], mask0, 127, 255, ThresholdTypes.Binary);
        foreach (var c in channels)
            c.Dispose();

        int d = (int)Math.Ceiling(Math.Sqrt((double)tw * tw + (double)th * th)) + 2;
        for (int angle = 0; angle < 360; angle += AngleStep)
        {
            using var m = Cv2.GetRotationMatrix2D(new Point2f(tw / 2f, th / 2f), angle, 1.0);
            m.Set(0, 2, m.Get<double>(0, 2) + d / 2.0 - tw / 2.0);
            m.Set(1, 2, m.Get<double>(1, 2) + d / 2.0 - th / 2.0);

            var g = new Mat();
            Cv2.WarpAffine(gray, g, m, new OpenCvSharp.Size(d, d), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            var mk = new Mat();
            Cv2.WarpAffine(mask0, mk, m, new OpenCvSharp.Size(d, d), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
            _rot[angle] = (g, mk);
        }

        _kernel3 = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        _kernel5 = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
    }

    /// <summary>
    /// Creates a tracker. cupTanWidth / cupTanArea describe one cup on screen (width and pixel area of
    /// its cup-coloured part, in screen pixels); ringOrigin is the screen position of the top-left corner
    /// of the frames that will be passed to <see cref="Update"/>.
    /// </summary>
    public static CupSpinTracker? TryCreate(
        string templatePath,
        double cupTanWidth,
        double cupTanArea,
        int ringOriginX,
        int ringOriginY,
        out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(templatePath))
            {
                error = $"Thiếu ảnh mẫu cái ly: {templatePath}";
                return null;
            }

            using var raw = Cv2.ImRead(templatePath, ImreadModes.Unchanged);
            if (raw.Empty())
            {
                error = $"Không đọc được ảnh mẫu cái ly: {templatePath}";
                return null;
            }

            using var bgra = new Mat();
            if (raw.Channels() == 4)
                raw.CopyTo(bgra);
            else if (raw.Channels() == 3)
                Cv2.CvtColor(raw, bgra, ColorConversionCodes.BGR2BGRA);
            else
            {
                error = "Ảnh mẫu cái ly phải là ảnh màu.";
                return null;
            }

            return new CupSpinTracker(bgra, cupTanWidth, cupTanArea, ringOriginX, ringOriginY);
        }
        catch (Exception ex)
        {
            error = $"Không khởi tạo được bộ theo dõi (OpenCV): {ex.Message}";
            return null;
        }
    }

    /// <summary>Measures one isolated cup in a frame: width and pixel area of its cup-coloured part.</summary>
    public static bool MeasureCup(Bitmap frame, Rectangle roi, out double tanWidth, out double tanArea)
    {
        tanWidth = 0;
        tanArea = 0;
        try
        {
            using var bgr = ToBgr(frame);
            var r = Rectangle.Intersect(roi, new Rectangle(0, 0, bgr.Cols, bgr.Rows));
            if (r.Width < 8 || r.Height < 8)
                return false;

            using var sub = new Mat(bgr, new Rect(r.X, r.Y, r.Width, r.Height));
            using var tan = TanMask(sub);
            tanArea = Cv2.CountNonZero(tan);
            tanWidth = Cv2.BoundingRect(tan).Width;
            return tanArea > 50 && tanWidth > 8;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Static check (no tracking): how many cups of the frame match the cup template at the best common
    /// angle. With the 8 upright cups before START this should be 8 at angle 0.
    /// </summary>
    public (int Cups, int Angle, double MeanScore) Probe(Bitmap frame)
    {
        using var bgr = ToBgr(frame);
        using var half = new Mat();
        Cv2.Resize(bgr, half, new OpenCvSharp.Size(0, 0), _k, _k, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(half, gray, ColorConversionCodes.BGR2GRAY);

        int bestCount = -1;
        int bestAngle = 0;
        double bestScore = 0;
        foreach (int a in _rot.Keys)
        {
            var peaks = Peaks(gray, a, 0.97, 8);
            double score = peaks.Sum(p => p.Score);
            if (peaks.Count > bestCount || (peaks.Count == bestCount && score > bestScore))
            {
                bestCount = peaks.Count;
                bestAngle = a;
                bestScore = score;
            }
        }

        return (Math.Max(0, bestCount), bestAngle, bestCount > 0 ? bestScore / bestCount : 0);
    }

    /// <summary>Sets the screen position where the diamond cup starts (centre of its slot).</summary>
    public void Start(double x, double y)
    {
        _px = x;
        _py = y;
        _vx = 0;
        _vy = 0;
        Phase = TrackPhase.Idle;
        Misses = 0;
        TotalMisses = 0;
        Frames = 0;
        _alignedStreak = 0;
        _theta = null;
    }

    /// <summary>
    /// Processes one captured frame (the area whose origin was given to TryCreate) and returns
    /// "idle" (nothing spinning yet), "seen" or "lost" (position was only predicted).
    /// </summary>
    public string Update(Bitmap ringFrame)
    {
        Frames++;
        using var bgr = ToBgr(ringFrame);
        using var half = new Mat();
        Cv2.Resize(bgr, half, new OpenCvSharp.Size(0, 0), _k, _k, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(half, gray, ColorConversionCodes.BGR2GRAY);

        // 1. The common angle = the angle at which the most (and best) cups match the template
        IEnumerable<int> angles = _theta is null
            ? _rot.Keys
            : _rot.Keys.Where(a => Math.Abs(AngleDiff(a, _theta.Value)) <= AngleWindow);

        int bestAngle = -1;
        double bestScore = -1;
        List<(double Score, double X, double Y)> bestPeaks = new();
        foreach (int a in angles)
        {
            var peaks = Peaks(gray, a, 0.95, 7);
            double score = peaks.Sum(p => p.Score);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = a;
                bestPeaks = peaks;
            }
        }

        _theta = bestAngle;

        // How many cups share that angle (strict threshold)?
        var strict = Peaks(gray, bestAngle, 0.97, 8);
        bool all8 = strict.Count >= 8;

        if (Phase == TrackPhase.Idle)
        {
            if (!all8)
                Phase = TrackPhase.Spinning;
        }
        else if (Phase == TrackPhase.Spinning)
        {
            _alignedStreak = all8 ? _alignedStreak + 1 : 0;
            if (_alignedStreak >= 2)
                Phase = TrackPhase.Aligned;
        }

        return Phase switch
        {
            TrackPhase.Idle => "idle",
            TrackPhase.Spinning => FollowResidual(half, bestPeaks, bestAngle),
            _ => FollowCups(strict)
        };
    }

    // ------------------------------------------------------------------------------------------------

    private string FollowResidual(Mat half, List<(double Score, double X, double Y)> peaks, int angle)
    {
        using var tan = TanMask(half);
        using var expl = new Mat(tan.Rows, tan.Cols, MatType.CV_8UC1, Scalar.All(0));
        var mask = _rot[angle].Mask;

        // 2. Draw every cup that matches the common angle
        foreach (var (_, cx, cy) in peaks)
        {
            int px = (int)Math.Round(cx - mask.Cols / 2.0);
            int py = (int)Math.Round(cy - mask.Rows / 2.0);
            int x0 = Math.Max(0, px);
            int y0 = Math.Max(0, py);
            int x1 = Math.Min(expl.Cols, px + mask.Cols);
            int y1 = Math.Min(expl.Rows, py + mask.Rows);
            if (x1 <= x0 || y1 <= y0)
                continue;

            using var dst = new Mat(expl, new Rect(x0, y0, x1 - x0, y1 - y0));
            using var src = new Mat(mask, new Rect(x0 - px, y0 - py, x1 - x0, y1 - y0));
            Cv2.BitwiseOr(dst, src, dst);
        }

        // 3. Whatever cup-coloured pixels remain belong to the cup that is NOT at the common angle
        using var explBig = new Mat();
        Cv2.Dilate(expl, explBig, _kernel5);
        using var inv = new Mat();
        Cv2.BitwiseNot(explBig, inv);
        using var resid = new Mat();
        Cv2.BitwiseAnd(tan, inv, resid);
        using var opened = new Mat();
        Cv2.MorphologyEx(resid, opened, MorphTypes.Open, _kernel3);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(opened, labels, stats, centroids, PixelConnectivity.Connectivity8);

        // 4. Pick the blob that best continues the track (gated by the predicted position)
        double predX = _px + _vx;
        double predY = _py + _vy;
        bool found = false;
        double bestCost = 9;
        double chosenX = 0;
        double chosenY = 0;
        for (int i = 1; i < n; i++)
        {
            double area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < 0.06 * _cupArea)
                continue;

            double cx = _ox + centroids.Get<double>(i, 0) / _k;
            double cy = _oy + centroids.Get<double>(i, 1) / _k;
            double dist = Math.Sqrt((cx - predX) * (cx - predX) + (cy - predY) * (cy - predY)) / CupWidth;
            double cost = dist - 0.5 * Math.Min(area / _cupArea, 1.0);
            if (dist < 1.3 && cost < bestCost)
            {
                bestCost = cost;
                found = true;
                chosenX = cx;
                chosenY = cy;
            }
        }

        return Apply(found, chosenX, chosenY);
    }

    private string FollowCups(List<(double Score, double X, double Y)> cups)
    {
        double predX = _px + _vx;
        double predY = _py + _vy;
        bool found = false;
        double bestDist = 9;
        double chosenX = 0;
        double chosenY = 0;
        foreach (var (_, cx, cy) in cups)
        {
            double x = _ox + cx / _k;
            double y = _oy + cy / _k;
            double dist = Math.Sqrt((x - predX) * (x - predX) + (y - predY) * (y - predY)) / CupWidth;
            if (dist < 0.8 && dist < bestDist)
            {
                bestDist = dist;
                found = true;
                chosenX = x;
                chosenY = y;
            }
        }

        return Apply(found, chosenX, chosenY);
    }

    private string Apply(bool found, double x, double y)
    {
        if (found)
        {
            _vx = 0.6 * (x - _px) + 0.4 * _vx;
            _vy = 0.6 * (y - _py) + 0.4 * _vy;
            _px = x;
            _py = y;
            Misses = 0;
            return "seen";
        }

        Misses++;
        TotalMisses++;
        _vx *= 0.6;
        _vy *= 0.6;
        _px += _vx;
        _py += _vy;
        return "lost";
    }

    private List<(double Score, double X, double Y)> Peaks(Mat gray, int angle, double threshold, int maxCount)
    {
        var (templ, mask) = _rot[angle];
        var list = new List<(double, double, double)>();
        if (gray.Cols < templ.Cols || gray.Rows < templ.Rows)
            return list;

        using var res = new Mat();
        Cv2.MatchTemplate(gray, templ, res, TemplateMatchModes.CCorrNormed, mask);
        Cv2.PatchNaNs(res, 0);

        int guard = maxCount + 8;
        while (list.Count < maxCount && guard-- > 0)
        {
            Cv2.MinMaxLoc(res, out _, out double score, out _, out OpenCvSharp.Point loc);
            if (score < threshold)
                break;

            if (score <= 1.0001)
                list.Add((score, loc.X + templ.Cols / 2.0, loc.Y + templ.Rows / 2.0));

            int x0 = Math.Max(0, loc.X - _nms);
            int y0 = Math.Max(0, loc.Y - _nms);
            int x1 = Math.Min(res.Cols, loc.X + _nms + 1);
            int y1 = Math.Min(res.Rows, loc.Y + _nms + 1);
            using var roi = new Mat(res, new Rect(x0, y0, x1 - x0, y1 - y0));
            roi.SetTo(Scalar.All(-1));
        }

        return list;
    }

    /// <summary>Cup-coloured pixels (tan wood): r >= 150, g >= 110, r - b >= 40. Returns a 0/255 mask.</summary>
    private static Mat TanMask(Mat bgr)
    {
        Cv2.Split(bgr, out Mat[] ch);
        try
        {
            using var mr = new Mat();
            using var mg = new Mat();
            using var md = new Mat();
            using var diff = new Mat();
            using var tmp = new Mat();
            var mask = new Mat();
            Cv2.Threshold(ch[2], mr, 149, 255, ThresholdTypes.Binary);
            Cv2.Threshold(ch[1], mg, 109, 255, ThresholdTypes.Binary);
            Cv2.Subtract(ch[2], ch[0], diff);
            Cv2.Threshold(diff, md, 39, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(mr, mg, tmp);
            Cv2.BitwiseAnd(tmp, md, mask);
            return mask;
        }
        finally
        {
            foreach (var c in ch)
                c.Dispose();
        }
    }

    private static Mat ToBgr(Bitmap bmp)
    {
        var mat = BitmapConverter.ToMat(bmp);
        if (mat.Channels() == 3)
            return mat;

        var bgr = new Mat();
        Cv2.CvtColor(mat, bgr, mat.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        mat.Dispose();
        return bgr;
    }

    private static int AngleDiff(int a, int b) => ((a - b + 540) % 360) - 180;

    public void Dispose()
    {
        foreach (var (g, m) in _rot.Values)
        {
            g.Dispose();
            m.Dispose();
        }

        _rot.Clear();
        _kernel3.Dispose();
        _kernel5.Dispose();
    }
}
