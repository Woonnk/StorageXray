using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StorageXray.Core;

namespace StorageXray.App;

public sealed record MapEntry(string Name, string Path, long Bytes, string Kind, FolderNode? Folder, FileRecord? File)
{
    public string SizeText => Sizes.Format(Bytes);
}
public sealed class Treemap : FrameworkElement
{
    private IReadOnlyList<MapEntry> entries = [];
    private readonly List<(Rect Rect, MapEntry Entry)> regions = [];
    private MapEntry? hovered;
    public event Action<MapEntry>? ItemClicked;
    public Treemap()
    {
        MouseMove += OnMove;
        MouseLeave += (_, _) => { hovered = null; InvalidateVisual(); };
        MouseLeftButtonUp += (_, e) => { var hit = Hit(e.GetPosition(this)); if (hit != null) ItemClicked?.Invoke(hit); };
        SizeChanged += (_, _) => InvalidateVisual();
        SnapsToDevicePixels = true;
    }
    public void SetFolder(FolderPage page)
    {
        var all = page.Items.Where(n => n.Bytes > 0).Select(n => new MapEntry(n.Name, n.Path, n.Bytes, n.Kind, n.Folder, n.File)).ToList();
        long restBytes = Math.Max(0, page.Folder.Bytes - all.Sum(e => e.Bytes));
        if (restBytes > 0) all.Add(new($"{page.TotalCount - all.Count:N0} smaller items", page.Folder.Path, restBytes, "Other", null, null));
        entries = all; hovered = null; ToolTip = null; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); regions.Clear();
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 33, 52)), null, new Rect(RenderSize));
        var layout = TreemapLayout.Arrange(entries.Select(e => e.Bytes).ToArray(), ActualWidth, ActualHeight);
        for (int i = 0; i < entries.Count; i++)
        {
            var box = layout[i]; var entry = entries[i];
            var rect = new Rect(box.X + 2, box.Y + 2, Math.Max(0, box.Width - 4), Math.Max(0, box.Height - 4));
            if (rect.Width < 1 || rect.Height < 1) continue;
            regions.Add((rect, entry));
            string category = entry.File?.Category ?? (entry.Folder == null ? "Other" : Dominant(entry.Folder));
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorFor(category)));
            dc.DrawRoundedRectangle(brush, entry == hovered ? new Pen(Brushes.White, 2) : null, rect, 5, 5);
            if (rect.Width < 65 || rect.Height < 38) continue;
            dc.PushClip(new RectangleGeometry(rect));
            var text = new FormattedText(entry.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, rect.Width - 20), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(rect.X + 10, rect.Y + 9));
            if (rect.Height > text.Height + 35)
            {
                var sub = new FormattedText(entry.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromArgb(220, 235, 245, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(sub, new Point(rect.X + 10, rect.Y + text.Height + 13));
            }
            dc.Pop();
        }
    }
    private static string Dominant(FolderNode folder) => FilePolicy.Category(folder.Path + "/_.none") == "Games" ? "Games" : "Folder";
    public static string ColorFor(string category) => category switch
    {
        "Games" => "#6951B1", "Videos" => "#2868A6", "Photos" => "#247D78", "Archives" => "#9D6535",
        "Documents" => "#436BB6", "Audio" => "#A14978", "Apps & system" => "#5A6478", "Folder" => "#325872", _ => "#475B73"
    };
    private MapEntry? Hit(Point point) => regions.FirstOrDefault(r => r.Rect.Contains(point)).Entry;
    private void OnMove(object sender, MouseEventArgs e)
    {
        var hit = Hit(e.GetPosition(this)); if (hit == hovered) return;
        hovered = hit; Cursor = hit?.Folder != null ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit == null ? null : hit.Path + "\n" + hit.SizeText;
        InvalidateVisual();
    }
}
