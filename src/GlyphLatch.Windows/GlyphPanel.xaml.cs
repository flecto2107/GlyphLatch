using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlyphLatch.Windows;

internal sealed record GlyphTile(GlyphTypeface Face, ushort Id, int? CodePoint, bool ById = false)
{
    public string ShortLabel => !ById && CodePoint is int code ? $"{code:X4}" : $"#{Id}";
    public string Label => CodePoint is int code ? $"U+{code:X4} · ID {Id}" : $"Глиф #{Id}";
    public string? Character => CodePoint is int code ? char.ConvertFromUtf32(code) : null;
}

public sealed class GlyphMark : FrameworkElement
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(nameof(Entry), typeof(object),
        typeof(GlyphMark), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush),
        typeof(GlyphMark), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public object? Entry { get => GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Entry is not GlyphTile tile || ActualWidth <= 7 || ActualHeight <= 7) return;
        try
        {
            var outline = tile.Face.GetGlyphOutline(tile.Id, 24, 24);
            var bounds = outline.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
            double scale = Math.Min(1, Math.Min((ActualWidth - 6) / bounds.Width, (ActualHeight - 5) / bounds.Height));
            drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale,
                (ActualWidth - bounds.Width * scale) / 2 - bounds.Left * scale,
                (ActualHeight - bounds.Height * scale) / 2 - bounds.Top * scale));
            try { drawing.DrawGeometry(Ink, null, outline); }
            finally { drawing.Pop(); }
        }
        catch (Exception error) when (FontPreviewCache.IsFontError(error)) { }
    }
}

public partial class GlyphPanel : UserControl
{
    private GlyphTile[] unicode = [];
    private GlyphTile[] all = [];
    private GlyphTile? selected;
    private string? active;
    private int revision;
    private bool loading;
    private string? problem;
    internal event EventHandler? CloseRequested;

    public GlyphPanel() => InitializeComponent();

    internal async Task ShowFont(FontEntry entry, FontPreviewCache cache)
    {
        string key = $"{entry.Id}|{entry.Path}|{entry.Available}";
        if (active == key) return;
        active = key;
        int request = ++revision;
        loading = true;
        problem = null;
        unicode = [];
        all = [];
        Select(null);
        Query.Clear();
        FaceName.Text = $"{entry.Family} · {entry.Face}";
        FaceName.ToolTip = entry.Path;
        UpdateTiles();
        try
        {
            var face = await cache.GetAsync(entry);
            if (request != revision) return;
            if (face == null)
            {
                problem = "Глифы этого файла недоступны.";
                return;
            }
            var coverage = await Task.Run(() => face.CharacterToGlyphMap
                .Where(pair => pair.Value != 0 && Rune.IsValid(pair.Key) && !Rune.IsControl(new Rune(pair.Key)))
                .OrderBy(pair => pair.Key).Select(pair => new GlyphTile(face, pair.Value, pair.Key)).ToArray());
            if (request != revision) return;
            unicode = coverage;
            var primary = coverage.DistinctBy(tile => tile.Id).ToDictionary(tile => tile.Id, tile => tile.CodePoint);
            all = Enumerable.Range(0, face.GlyphCount).Select(id =>
                new GlyphTile(face, (ushort)id, primary.TryGetValue((ushort)id, out int? code) ? code : null, true)).ToArray();
        }
        catch (Exception error) when (FontPreviewCache.IsFontError(error))
        {
            if (request == revision) problem = "Не удалось прочитать глифы.";
        }
        finally
        {
            if (request == revision)
            {
                loading = false;
                UpdateTiles();
            }
        }
    }

    internal void Clear()
    {
        revision++;
        active = null;
        loading = false;
        problem = null;
        unicode = [];
        all = [];
        Select(null);
        Tiles.ItemsSource = null;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (Tiles == null) return;
        Select(null);
        UpdateTiles();
    }

    private void UpdateTiles()
    {
        string query = Query.Text.Trim();
        QueryHint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var source = AllIds.IsChecked == true ? all : unicode;
        var filtered = query.Length == 0 ? source : source.Where(tile => Matches(tile, query)).ToArray();
        Tiles.ItemsSource = filtered.Chunk(5).ToArray();
        if (Tiles.Items.Count > 0) Tiles.ScrollIntoView(Tiles.Items[0]);
        CountText.Text = loading ? "Загрузка…" : $"{filtered.Length:N0} {(AllIds.IsChecked == true ? "глифов" : "символов")}";
        EmptyText.Text = loading ? "Читаю таблицу символов…" : problem ?? "Совпадений нет";
        EmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(GlyphTile tile, string query)
    {
        if (query.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(query.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
                && tile.CodePoint == code;
        if (query[0] == '#')
            return ushort.TryParse(query.AsSpan(1), out ushort id) && tile.Id == id;
        return tile.Character is string text && query.Contains(text, StringComparison.Ordinal)
            || tile.ShortLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void PickGlyph(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GlyphTile tile }) Select(tile);
    }

    private void Select(GlyphTile? tile)
    {
        selected = tile;
        SelectedText.Text = tile?.Label ?? "Выберите глиф";
        CopyButton.IsEnabled = tile?.Character != null;
    }

    private void CopyGlyph(object sender, RoutedEventArgs e)
    {
        if (selected?.Character is not string character) return;
        try { Clipboard.SetText(character); SelectedText.Text = $"{selected.Label} · скопировано"; }
        catch (System.Runtime.InteropServices.COMException) { SelectedText.Text = "Буфер обмена занят"; }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
