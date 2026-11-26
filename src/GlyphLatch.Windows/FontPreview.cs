using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace GlyphLatch.Windows;

public sealed class FontPreview : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(object),
        typeof(FontPreview), new FrameworkPropertyMetadata(null, (d, _) => ((FontPreview)d).RefreshFace()));
    public static readonly DependencyProperty SampleProperty = DependencyProperty.Register(nameof(Sample), typeof(string),
        typeof(FontPreview), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double),
        typeof(FontPreview), new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush),
        typeof(FontPreview), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(nameof(Muted), typeof(Brush),
        typeof(FontPreview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string Sample { get => (string)GetValue(SampleProperty); set => SetValue(SampleProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public Brush Muted { get => (Brush)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public bool Wrap { get; init; }

    private GlyphTypeface? face;
    private int revision;
    private string? unavailable;

    public FontPreview()
    {
        Loaded += (_, _) => RefreshFace();
        Unloaded += (_, _) => { revision++; face = null; };
    }

    private async void RefreshFace()
    {
        int request = ++revision;
        face = null;
        unavailable = null;
        if (Wrap) InvalidateMeasure();
        InvalidateVisual();
        if (!IsLoaded || Source is not FontEntry entry || Window.GetWindow(this) is not MainWindow window) return;
        if (!entry.Available)
        {
            unavailable = entry.Issue ?? "Файл недоступен";
            InvalidateVisual();
            return;
        }
        GlyphTypeface? loaded = await window.PreviewCache.GetAsync(entry);
        if (request != revision) return;
        face = loaded;
        if (face == null) unavailable = "Не удалось прочитать контуры шрифта";
        if (Wrap) InvalidateMeasure();
        InvalidateVisual();
    }

    private readonly record struct Cell(bool Space, ushort? Id, double Width);

    private Cell[] GetCells(string text, GlyphTypeface typeface) => text.EnumerateRunes()
        .Where(rune => !Rune.IsControl(rune)).Select(rune =>
        {
            double advance = 0;
            bool exists = typeface.CharacterToGlyphMap.TryGetValue(rune.Value, out ushort id) && id != 0 &&
                typeface.AdvanceWidths.TryGetValue(id, out advance) && double.IsFinite(advance) && advance >= 0;
            return new Cell(Rune.IsWhiteSpace(rune), exists ? id : null,
                exists ? advance * Size : Size * .52);
        }).ToArray();

    private IEnumerable<ArraySegment<Cell>> GetLines(GlyphTypeface typeface, double width)
    {
        foreach (string paragraph in Sample.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var cells = GetCells(paragraph, typeface);
            if (cells.Length == 0) yield return new ArraySegment<Cell>(cells);
            int start = 0;
            while (start < cells.Length)
            {
                int end = start;
                int lastSpace = -1;
                double lineWidth = 0;
                while (end < cells.Length && (end == start || lineWidth + cells[end].Width <= width))
                {
                    if (cells[end].Space) lastSpace = end;
                    lineWidth += cells[end++].Width;
                }
                if (end < cells.Length && lastSpace > start) end = lastSpace + 1;
                yield return new ArraySegment<Cell>(cells, start, end - start);
                start = end;
            }
        }
    }

    private double LineHeight(GlyphTypeface typeface) => Math.Ceiling(Size * Math.Max(1.18, typeface.Height) + 3);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!Wrap) return base.MeasureOverride(availableSize);
        if (face == null) return new Size(0, Source == null ? 0 : 23);
        try { return new Size(0, LineHeight(face) * GetLines(face, availableSize.Width).Count()); }
        catch (Exception error) when (FontPreviewCache.IsFontError(error)) { return new Size(0, 23); }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (face == null)
        {
            if (unavailable != null) DrawNote(drawing, unavailable, Muted, dpi);
            return;
        }

        drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        try
        {
            if (Sample.Length == 0) return;
            double height = Wrap ? LineHeight(face) : ActualHeight;
            double baseline = (height - Size * face.Height) / 2 + Size * face.Baseline;
            if (Wrap)
            {
                foreach (var line in GetLines(face, ActualWidth))
                {
                    PaintLine(line, baseline, false);
                    baseline += height;
                }
                return;
            }
            PaintLine(GetCells(Sample.Replace('\r', ' ').Replace('\n', ' '), face), baseline, true);
        }
        catch (Exception error) when (FontPreviewCache.IsFontError(error))
        {
            DrawNote(drawing, "Не удалось отрисовать контуры шрифта", Muted, dpi);
        }
        finally { drawing.Pop(); }

        void PaintLine(IReadOnlyList<Cell> cells, double baseline, bool ellipsis)
        {
            var typeface = face!;
            var ids = new List<ushort>();
            var advances = new List<double>();
            double x = 0;
            double start = 0;
            if (ellipsis && !cells.Any(cell => cell.Id.HasValue && !cell.Space))
            {
                DrawNote(drawing, "Нет символов для этого образца", Muted, dpi);
                return;
            }

            void Paint()
            {
                if (ids.Count == 0) return;
                drawing.DrawGlyphRun(Ink, new GlyphRun(typeface, 0, false, Size, (float)dpi, ids.ToArray(),
                    new Point(start, baseline), advances.ToArray(), null, null, null, null, null, null));
                ids.Clear();
                advances.Clear();
            }

            bool truncated = ellipsis && cells.Sum(cell => cell.Width) > ActualWidth;
            double limit = truncated ? Math.Max(0, ActualWidth - 17) : ActualWidth;
            foreach (var cell in cells)
            {
                if (x + cell.Width > limit && x > 0) break;
                if (cell.Id is ushort id)
                {
                    if (ids.Count == 0) start = x;
                    ids.Add(id);
                    advances.Add(cell.Width);
                }
                else
                {
                    Paint();
                    if (!cell.Space)
                        drawing.DrawRectangle(null, new Pen(Muted, 1),
                            new Rect(x + Size * .1, baseline - Size * .52, Size * .32, Size * .4));
                }
                x += cell.Width;
            }
            Paint();
            if (truncated) DrawNote(drawing, "…", Muted, dpi, Math.Max(x, limit));
        }
    }

    private void DrawNote(DrawingContext drawing, string note, Brush brush, double dpi, double x = 0) =>
        drawing.DrawText(new FormattedText(note, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Window.GetWindow(this)?.FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal), 13, brush, dpi), new Point(x, Math.Max(0, (ActualHeight - 18) / 2)));
}
