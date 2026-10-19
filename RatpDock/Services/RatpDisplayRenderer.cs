using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace RatpDock;

/// <summary>
/// Renders mini RATP passenger-information boards (SIEL style) as bitmaps:
/// amber #FFCE00 text on a black board, set in a **dot-matrix LED typeface**
/// ("Dot Matrix", the font used by physical SIEL recreations such as
/// ratp-display-pi) — RATP's actual LED typefaces (Parisine Girouette by
/// ZeCraft) are proprietary and unavailable, but the dot-matrix look is the
/// signature of the real SIEL boards. Line badges use RATP line colors.
/// The dock band only renders Title + Icon, so the board IS the icon.
/// Icons are cached per visual state — reassigning a fresh IconInfo on every
/// poll would make the dock flicker.
/// </summary>
internal static partial class RatpDisplayRenderer
{
    // Classic RATP "LED Orange" waiting-time amber on black.
    private static readonly Color Amber = ColorTranslator.FromHtml("#FFCE00");
    private static readonly Color AmberDim = ColorTranslator.FromHtml("#B08D00");
    private static readonly Color BoardBlack = Color.FromArgb(255, 8, 8, 8);

    // RATP / Paris metro line colors (curated palette used by the NextMetro
    // SIEL recreation). Keyed by upper-cased line label without prefix.
    private static readonly Dictionary<string, (Color Bg, bool DarkText)> LineColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = (ColorTranslator.FromHtml("#FFCD00"), true),
        ["2"] = (ColorTranslator.FromHtml("#003CA6"), false),
        ["3"] = (ColorTranslator.FromHtml("#837902"), true),
        ["3B"] = (ColorTranslator.FromHtml("#98D4E2"), true),
        ["4"] = (ColorTranslator.FromHtml("#BE4192"), false),
        ["5"] = (ColorTranslator.FromHtml("#F28E42"), true),
        ["6"] = (ColorTranslator.FromHtml("#83C491"), true),
        ["7"] = (ColorTranslator.FromHtml("#F3A4BA"), true),
        ["7B"] = (ColorTranslator.FromHtml("#83C491"), true),
        ["8"] = (ColorTranslator.FromHtml("#CEADD2"), true),
        ["9"] = (ColorTranslator.FromHtml("#D5C900"), true),
        ["10"] = (ColorTranslator.FromHtml("#E3B32A"), true),
        ["11"] = (ColorTranslator.FromHtml("#8D5E2A"), true),
        ["12"] = (ColorTranslator.FromHtml("#007852"), false),
        ["13"] = (ColorTranslator.FromHtml("#98D4E2"), true),
        ["14"] = (ColorTranslator.FromHtml("#62259D"), false),
        ["A"] = (ColorTranslator.FromHtml("#E3051C"), false),
        ["B"] = (ColorTranslator.FromHtml("#5291CE"), false),
        ["C"] = (ColorTranslator.FromHtml("#C99B2E"), true),
        ["D"] = (ColorTranslator.FromHtml("#6E3B1F"), false),
        ["E"] = (ColorTranslator.FromHtml("#E1B7A0"), true),
    };

    private static readonly Color DefaultBadge = Color.FromArgb(255, 60, 60, 60);

    private static readonly object FontLock = new();
    private static PrivateFontCollection? _fontCollection;
    private static Font? _boldTall;
    private static Font? _boldText;

    private static readonly Dictionary<string, IconInfo> IconCache = new(StringComparer.Ordinal);

    /// <summary>Dock band icon: square mini-board with the journey time in amber.</summary>
    public static IconInfo DockIcon(JourneyStatus status)
    {
        var key = $"dock|{status.Route.Id}|{status.DurationText}";
        return GetOrRender(key, () => RenderDockBoard(status));
    }

    /// <summary>Palette page icon: wider board with time, line badges and departure time.</summary>
    public static IconInfo PageIcon(JourneyStatus status)
    {
        var dep = status.Departure?.LocalDateTime.ToString("HH:mm");
        var key = $"page|{status.Route.Id}|{status.DurationText}|{dep}|{string.Join(",", status.Lines)}";
        return GetOrRender(key, () => RenderPageBoard(status));
    }

    private static IconInfo GetOrRender(string key, Func<byte[]> render)
    {
        lock (IconCache)
        {
            if (IconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var icon = IconInfoFromPng(render());
            IconCache[key] = icon;
            if (IconCache.Count > 128)
            {
                // simple bound so a long session can't grow it forever
                foreach (var stale in IconCache.Keys.Take(32).ToList())
                {
                    IconCache.Remove(stale);
                }
            }

            return icon;
        }
    }

    private static byte[] RenderDockBoard(JourneyStatus status)
    {
        const int size = 128;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        FillBoard(g, size, size);

        // Real SIEL boards: just the number of minutes, centered, nothing
        // else. Digits use the tall dot-matrix face (digits only); the error
        // and loading placeholders use the text face.
        var text = status.Error is not null ? "!" : status.DurationMinutes is { } m ? $"{m}" : "..";
        var digitsOnly = text.All(char.IsDigit);

        using var amber = new SolidBrush(Amber);
        var font = digitsOnly ? BoldTall(size * 0.62f) : Bold(size * 0.50f);
        var measured = g.MeasureString(text, font);
        g.DrawString(text, font, amber, (size - measured.Width) / 2f, (size - measured.Height) / 2f);

        return ToPng(bmp);
    }

    private static byte[] RenderPageBoard(JourneyStatus status)
    {
        const int width = 256;
        const int height = 128;
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        FillBoard(g, width, height);

        if (status.Error is not null)
        {
            using var amber = new SolidBrush(Amber);
            var font = Bold(height * 0.30f);
            var text = "INDISPO";
            var measured = g.MeasureString(text, font);
            g.DrawString(text, font, amber, (width - measured.Width) / 2f, (height - measured.Height) / 2f);
            return ToPng(bmp);
        }

        using var amberBrush = new SolidBrush(Amber);
        using var dimBrush = new SolidBrush(AmberDim);

        // Big amber duration, RATP waiting-time style ("47 MIN"). The dot
        // matrix face has no accented glyphs — SIEL messages are unaccented
        // caps ("DEP", not "DÉP").
        var duration = status.DurationMinutes is { } m ? $"{m} MIN" : "..";
        var big = Bold(height * 0.42f);
        g.DrawString(duration, big, amberBrush, width * 0.05f, height * 0.08f);

        // Departure time, smaller, right of the duration.
        if (status.Departure is { } dep)
        {
            var text = $"DEP {dep.LocalDateTime:HH\\:mm}";
            var small = Bold(height * 0.20f);
            var measured = g.MeasureString(text, small);
            g.DrawString(text, small, dimBrush, width * 0.05f, height * 0.58f);
            _ = measured;
        }

        // Line badges along the bottom, left to right.
        DrawBadges(g, status.LineBadges, width, y: height * 0.66f, badgeSize: height * 0.28f, spacing: height * 0.05f);

        return ToPng(bmp);
    }

    /// <summary>Black rounded board filling the whole bitmap.</summary>
    private static void FillBoard(Graphics g, int width, int height)
    {
        g.Clear(Color.Transparent);
        using var path = RoundedRect(new RectangleF(0, 0, width, height), Math.Min(width, height) * 0.12f);
        using var bg = new SolidBrush(BoardBlack);
        g.FillPath(bg, path);
    }

    /// <summary>Rounded line badges in RATP colors (official line colors when the data source provides them), clipped to the board width.</summary>
    private static void DrawBadges(Graphics g, IReadOnlyList<LineBadge> badges, float boardWidth, float y, float badgeSize, float spacing)
    {
        var distinct = badges
            .Select(b => (Label: NormalizeLine(b.Label), b.BackgroundHex, b.DarkText))
            .Where(b => b.Label.Length > 0)
            .GroupBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
            .Select(grp => grp.First())
            .Take(6)
            .ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var x = spacing;
        foreach (var badge in distinct)
        {
            if (x + badgeSize > boardWidth)
            {
                break;
            }

            // Curated RATP palette first, then the official color from the data
            // source, then the neutral default.
            var (bg, darkText) = LineColors.TryGetValue(badge.Label, out var color)
                ? color
                : badge.BackgroundHex is { Length: > 0 } hex
                    ? (ColorTranslator.FromHtml(hex), badge.DarkText is true)
                    : (DefaultBadge, false);
            using var path = RoundedRect(new RectangleF(x, y, badgeSize, badgeSize), badgeSize * 0.25f);
            using var bgBrush = new SolidBrush(bg);
            g.FillPath(bgBrush, path);

            using var textBrush = new SolidBrush(darkText ? Color.Black : Color.White);
            var font = Bold(badgeSize * 0.55f);
            var label = ShortLabel(badge.Label);
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, textBrush, x + (badgeSize - size.Width) / 2f, y + (badgeSize - size.Height) / 2f);

            x += badgeSize + spacing;
        }
    }

    /// <summary>Strips common prefixes ("M9" → "9", "RER B" → "B") to match the color table.</summary>
    private static string NormalizeLine(string line) => line.Trim().TrimStart('M', 'm').Trim();

    private static string ShortLabel(string line)
    {
        var l = line.Trim();
        return l.Length <= 3 ? l : l[..3];
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Dot-matrix face for chart labels (shared with JourneyChart).</summary>
    internal static Font ChartFont(float size) => Bold(size);

    /// <summary>Tall dot-matrix face — digits only (the dock board number).</summary>
    private static Font BoldTall(float size)
    {
        lock (FontLock)
        {
            EnsureFont();
            return new Font(_boldTall!.FontFamily, size, _boldTall.Style);
        }
    }

    /// <summary>Regular-height dot-matrix face — letters, digits, punctuation.</summary>
    private static Font Bold(float size)
    {
        lock (FontLock)
        {
            EnsureFont();
            return new Font(_boldText!.FontFamily, size, _boldText.Style);
        }
    }

    private static void EnsureFont()
    {
        if (_boldText is not null)
        {
            return;
        }

        _fontCollection = new PrivateFontCollection();
        _boldTall = LoadFont("DotMatrixBoldTall.ttf");
        _boldText = LoadFont("DotMatrixBold.ttf");

        Font LoadFont(string file)
        {
            var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (File.Exists(fontPath))
            {
                _fontCollection!.AddFontFile(fontPath);
                var family = _fontCollection.Families[^1];
                return new Font(family, 24, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            // Fallback: Segoe UI Bold (never expected in the packaged app).
            return new Font("Segoe UI", 24, FontStyle.Bold, GraphicsUnit.Pixel);
        }
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static IconInfo IconInfoFromPng(byte[] png)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            dw.WriteBytes(png);
            _ = dw.StoreAsync().AsTask().GetAwaiter().GetResult();
            _ = dw.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        return IconInfo.FromStream(ras);
    }
}
