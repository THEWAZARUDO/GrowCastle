namespace GrowCastleAuto.Forms;

public sealed class PreviewForm : Form
{
    private readonly Bitmap _previewImage;

    public PreviewForm(string title, Bitmap annotatedImage)
    {
        _previewImage = annotatedImage;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(Math.Min(1200, annotatedImage.Width + 40), Math.Min(820, annotatedImage.Height + 60));
        BackColor = Color.FromArgb(18, 22, 31);

        var pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = _previewImage,
            SizeMode = PictureBoxSizeMode.Zoom
        };
        Controls.Add(pic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _previewImage.Dispose();
        base.Dispose(disposing);
    }
}
