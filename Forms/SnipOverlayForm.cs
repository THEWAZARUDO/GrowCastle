using System.Drawing.Drawing2D;
using GrowCastleAuto.Native;

namespace GrowCastleAuto.Forms;

public sealed class SnipOverlayForm : Form
{
    private readonly Bitmap _screenSnapshot;
    private readonly Rectangle _virtualBounds;
    private readonly string _promptText;

    private bool _isDragging;
    private Point _startPt;
    private Point _currentPt;

    public Bitmap? CapturedBitmap { get; private set; }
    public Rectangle SelectedScreenRect { get; private set; }

    public SnipOverlayForm(string promptText)
    {
        _promptText = promptText;
        _virtualBounds = Win32.GetVirtualScreenBounds();
        _screenSnapshot = new Bitmap(_virtualBounds.Width, _virtualBounds.Height);
        using (var g = Graphics.FromImage(_screenSnapshot))
        {
            g.CopyFromScreen(_virtualBounds.Left, _virtualBounds.Top, 0, 0, _virtualBounds.Size, CopyPixelOperation.SourceCopy);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _startPt = e.Location;
            _currentPt = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging)
        {
            _currentPt = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isDragging || e.Button != MouseButtons.Left)
            return;

        _isDragging = false;
        _currentPt = e.Location;

        var rect = GetSelectionRect();
        if (rect.Width < 5 || rect.Height < 5)
        {
            Invalidate();
            return;
        }

        CapturedBitmap = _screenSnapshot.Clone(rect, _screenSnapshot.PixelFormat);
        SelectedScreenRect = new Rectangle(
            _virtualBounds.Left + rect.Left,
            _virtualBounds.Top + rect.Top,
            rect.Width,
            rect.Height);

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.DrawImageUnscaled(_screenSnapshot, 0, 0);

        using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        var sel = GetSelectionRect();

        if (_isDragging && sel.Width > 1 && sel.Height > 1)
        {
            using var region = new Region(new Rectangle(0, 0, Width, Height));
            region.Exclude(sel);
            g.FillRegion(dimBrush, region);

            using var borderPen = new Pen(Color.FromArgb(0, 220, 140), 2f) { DashStyle = DashStyle.Solid };
            g.DrawRectangle(borderPen, sel);

            string sizeText = $"{sel.Width} × {sel.Height} px";
            using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var textSize = g.MeasureString(sizeText, font);
            int tx = sel.Left;
            int ty = sel.Top - (int)textSize.Height - 6;
            if (ty < 4) ty = sel.Bottom + 6;
            var labelRect = new RectangleF(tx, ty, textSize.Width + 10, textSize.Height + 4);
            using var labelBg = new SolidBrush(Color.FromArgb(210, 15, 23, 42));
            g.FillRectangle(labelBg, labelRect);
            g.DrawString(sizeText, font, Brushes.LimeGreen, tx + 5, ty + 2);
        }
        else
        {
            g.FillRectangle(dimBrush, 0, 0, Width, Height);
        }

        // Top instruction banner
        using var bannerFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        string msg = $"{_promptText}  |  Kéo chuột để chọn vùng  |  ESC hoặc Chuột phải để hủy";
        var msgSize = g.MeasureString(msg, bannerFont);
        float bx = Math.Max(20, (Width - msgSize.Width) / 2f);
        var bannerRect = new RectangleF(bx - 14, 16, msgSize.Width + 28, msgSize.Height + 14);
        using var bannerBrush = new SolidBrush(Color.FromArgb(225, 17, 24, 39));
        using var bannerBorder = new Pen(Color.FromArgb(56, 189, 248), 1.5f);
        g.FillRectangle(bannerBrush, bannerRect);
        g.DrawRectangle(bannerBorder, bannerRect.X, bannerRect.Y, bannerRect.Width, bannerRect.Height);
        g.DrawString(msg, bannerFont, Brushes.White, bx, 23);
    }

    private Rectangle GetSelectionRect()
    {
        int x = Math.Min(_startPt.X, _currentPt.X);
        int y = Math.Min(_startPt.Y, _currentPt.Y);
        int w = Math.Abs(_currentPt.X - _startPt.X);
        int h = Math.Abs(_currentPt.Y - _startPt.Y);
        return Rectangle.Intersect(new Rectangle(0, 0, Width, Height), new Rectangle(x, y, w, h));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _screenSnapshot.Dispose();
        base.Dispose(disposing);
    }
}
