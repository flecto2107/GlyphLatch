using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GlyphLatch.Windows;

public partial class MainWindow
{
    private Geometry[]? loadingGlyphs;
    private DispatcherTimer? loadingAnimation;
    private IInputElement? previousScanFocus;
    private int loadingIndex;
    private static readonly Duration GlyphEaseDuration = new(TimeSpan.FromMilliseconds(180));

    private async Task ShowLoadingAfterDelay(CancellationTokenSource operation)
    {
        try { await Task.Delay(140, operation.Token); }
        catch (OperationCanceledException) { return; }
        if (scan != operation || exiting || LoadingOverlay.Visibility == Visibility.Visible) return;

        loadingGlyphs ??= ReadLoadingGlyphs();
        previousScanFocus = Keyboard.FocusedElement;
        WorkspaceSurface.Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance };
        WorkspaceSurface.IsHitTestVisible = false;
        LoadingOverlay.Visibility = Visibility.Visible;
        CancelScanButton.Focus();
        loadingIndex = 0;
        DrawLoadingGlyph();
        if (loadingGlyphs.Length > 1 && SystemParameters.ClientAreaAnimation)
        {
            loadingAnimation ??= Timer(510, () =>
            {
                loadingIndex = (loadingIndex + 1) % loadingGlyphs.Length;
                DrawLoadingGlyph();
            });
            loadingAnimation.Start();
        }
    }

    private void DrawLoadingGlyph()
    {
        LoadingGlyph.Data = loadingGlyphs![loadingIndex];
        if (!SystemParameters.ClientAreaAnimation) return;
        LoadingGlyph.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, GlyphEaseDuration));
        LoadingScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.92, 1, GlyphEaseDuration));
        LoadingScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.92, 1, GlyphEaseDuration));
    }

    private void HideLoading()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) return;
        loadingAnimation?.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        WorkspaceSurface.Effect = null;
        WorkspaceSurface.IsHitTestVisible = true;
        if (!exiting && previousScanFocus is UIElement { IsVisible: true, IsEnabled: true })
            Keyboard.Focus(previousScanFocus);
        previousScanFocus = null;
    }

    private static Geometry[] ReadLoadingGlyphs()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "LoadingGlyphs.local.txt");
        var glyphs = new List<Geometry>();
        try
        {
            if (File.Exists(local) && new FileInfo(local).Length <= 96 * 1024)
            {
                foreach (string line in File.ReadLines(local))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var glyph = Geometry.Parse(line);
                    if (!glyph.Bounds.IsEmpty) glyphs.Add(glyph);
                    if (glyphs.Count == 12) break;
                }
            }
        }
        catch (Exception error) when (FontPreviewCache.IsFontError(error)) { glyphs.Clear(); }
        if (glyphs.Count > 0) return glyphs.ToArray();

        glyphs.Add(VectorAssets.BrandMark());
        foreach (string family in new[] { "Georgia", "Impact", "Gabriola", "Courier New", "Arial Black" })
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out var glyphFace) && glyphFace.CharacterToGlyphMap.TryGetValue('G', out ushort id))
                glyphs.Add(glyphFace.GetGlyphOutline(id, 1, 1));
        }
        return glyphs.ToArray();
    }
}
