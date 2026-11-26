using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace GlyphLatch.Windows;

internal static class VectorAssets
{
    private static readonly Dictionary<string, Geometry> lucide = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DrawingImage> brand = new(StringComparer.Ordinal);
    private static readonly Dictionary<bool, BitmapSource> captions = [];

    private static Uri Asset(string path) =>
        new($"/{typeof(VectorAssets).Assembly.GetName().Name};component/Assets/{path}", UriKind.Relative);

    private static XElement Load(string path)
    {
        using var stream = Application.GetResourceStream(Asset(path)).Stream;
        return XElement.Load(stream);
    }

    internal static DrawingImage Logo(bool darkTheme)
    {
        string name = darkTheme ? "logo_light" : "logo_dark";
        if (brand.TryGetValue(name, out var cached)) return cached;
        var svg = Load($"Brand/{name}.svg");
        string paint = Regex.Match(svg.Descendants().Single(node => node.Name.LocalName == "style").Value,
            @"fill:\s*(#[0-9a-fA-F]{6})").Groups[1].Value;
        var path = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var element in svg.Descendants().Where(node => node.Name.LocalName == "path"))
            path.Children.Add(Geometry.Parse("F1 " + (string?)element.Attribute("d")));
        var ink = new SolidColorBrush((Color)ColorConverter.ConvertFromString(paint));
        var drawing = new DrawingGroup();
        var bounds = path.Bounds;
        bounds.Inflate(1, 1);
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(bounds)));
        drawing.Children.Add(new GeometryDrawing(ink, null, path));
        var image = new DrawingImage(drawing);
        image.Freeze();
        brand.Add(name, image);
        return image;
    }

    internal static Geometry BrandMark()
    {
        var svg = Load("Brand/icon_dark.svg");
        return Geometry.Parse("F1 " + (string?)svg.Descendants().Single(node => node.Name.LocalName == "path").Attribute("d"));
    }

    internal static Uri WindowIcon(bool darkSurface) => Asset($"Brand/{(darkSurface ? "icon_light" : "icon_dark")}.ico");

    internal static BitmapSource CaptionIcon(bool darkSurface)
    {
        if (captions.TryGetValue(darkSurface, out var cached)) return cached;
        using var stream = Application.GetResourceStream(WindowIcon(darkSurface)).Stream;
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        captions.Add(darkSurface, bitmap);
        return bitmap;
    }

    internal static System.Drawing.Icon TrayIcon(bool darkSurface)
    {
        using var stream = Application.GetResourceStream(WindowIcon(darkSurface)).Stream;
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    internal static Geometry Icon(string glyph)
    {
        if (lucide.TryGetValue(glyph, out var cached)) return cached;
        var group = new GeometryGroup();
        foreach (var node in Load($"Lucide/{glyph}.svg").Elements()) group.Children.Add(ToGeometry(node));
        group.Freeze();
        lucide.Add(glyph, group);
        return group;
    }

    private static Geometry ToGeometry(XElement node)
    {
        double Value(string name) => double.Parse((string?)node.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
        return node.Name.LocalName switch
        {
            "path" => Geometry.Parse((string?)node.Attribute("d") ?? ""),
            "line" => new LineGeometry(new Point(Value("x1"), Value("y1")), new Point(Value("x2"), Value("y2"))),
            "circle" => new EllipseGeometry(new Point(Value("cx"), Value("cy")), Value("r"), Value("r")),
            "ellipse" => new EllipseGeometry(new Point(Value("cx"), Value("cy")), Value("rx"), Value("ry")),
            "rect" => new RectangleGeometry(new Rect(Value("x"), Value("y"), Value("width"), Value("height")),
                Value("rx"), node.Attribute("ry") == null ? Value("rx") : Value("ry")),
            "polyline" => Geometry.Parse("M" + (string?)node.Attribute("points")),
            "polygon" => Geometry.Parse("M" + (string?)node.Attribute("points") + "Z"),
            _ => throw new NotSupportedException($"Неподдерживаемый элемент Lucide: {node.Name.LocalName}")
        };
    }

    internal static void DrawIcon(DrawingContext drawing, string glyph, Brush ink, double width, double height)
    {
        double scale = Math.Min(width, height) / 24;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, (width - 24 * scale) / 2, (height - 24 * scale) / 2));
        var pen = new Pen(ink, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.DrawGeometry(null, pen, Icon(glyph));
        drawing.Pop();
    }

    internal static System.Drawing.Bitmap MenuIcon(string glyph, System.Drawing.Color ink)
    {
        var brush = new SolidColorBrush(Color.FromArgb(ink.A, ink.R, ink.G, ink.B));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) DrawIcon(drawing, glyph, brush, 16, 16);
        var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        png.Save(stream);
        stream.Position = 0;
        using var image = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(image);
    }
}

public sealed class LucideIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string),
        typeof(LucideIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public LucideIcon()
    {
        Width = Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        if (!string.IsNullOrEmpty(Glyph) && ActualWidth > 0 && ActualHeight > 0)
            VectorAssets.DrawIcon(drawing, Glyph, Foreground, ActualWidth, ActualHeight);
    }
}
