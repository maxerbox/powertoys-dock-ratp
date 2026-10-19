using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace RatpDock;

/// <summary>
/// Renders the journey-duration history as a SIEL-style chart: amber
/// dot-matrix on a black board. Two panels: (1) duration vs time of day —
/// one dot per recorded sample, a bold line through the 30-minute-bucket
/// averages, and a marker at the current time; (2) average duration per
/// day of week — the weekly comparison. The "when should I leave" view.
/// The PNG is written to
/// <c>%USERPROFILE%\.ratpdock\charts\&lt;routeId&gt;.png</c> and used as the
/// details-pane HeroImage (the palette shows it when the item is selected /
/// hovered, via <c>ShowDetails</c>).
/// </summary>
internal static class JourneyChart
{
    private static readonly Color Amber = ColorTranslator.FromHtml("#FFCE00");
    private static readonly Color AmberDim = ColorTranslator.FromHtml("#B08D00");
    private static readonly Color BoardBlack = Color.FromArgb(255, 8, 8, 8);

    private static readonly string ChartDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ratpdock",
        "charts");

    private const int Width = 900;
    private const int Height = 920;
    private const float Left = 96;
    private const float Right = Width - 44;
    private const float Top = 92;
    private const float Bottom = Height - 64;

    // Second panel: average duration by day of week (the weekly comparison).
    private const float WeekTitleY = 540;
    private const float WeekTop = 640;
    private const float WeekBottom = 856;
    private const float WeekLabelY = 866;

    /// <summary>X domain: 05:00 → 23:00 (commuter hours; samples outside are ignored).</summary>
    private const int DayStartMinutes = 5 * 60;
    private const int DayEndMinutes = 23 * 60;
    private const int BucketMinutes = 30;

    /// <summary>Statistics distilled from the samples for the details body.</summary>
    internal sealed record Stats(
        int SampleCount,
        int Days,
        int? CurrentBucketAverage,
        string? BestLabel,
        int? BestAverage,
        string? WorstLabel,
        int? WorstAverage,
        IReadOnlyList<DayStat> DayStats,
        string? BestDayLabel,
        int? BestDayAverage,
        string? WorstDayLabel,
        int? WorstDayAverage);

    /// <summary>Average duration for one day of the week (null when no data).</summary>
    internal sealed record DayStat(string Label, int? Average, int Count);

    /// <summary>Renders the chart PNG for a route and returns its path (null while there is not enough data).</summary>
    public static string? Render(string routeId, IReadOnlyList<JourneyHistoryStore.Sample> samples)
    {
        if (samples.Count < 4)
        {
            return null; // not enough data for a meaningful chart yet
        }

        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        FillBoard(g);

        var minutes = samples.Select(s => s.Minutes).ToList();
        var yMax = (int)(Math.Ceiling((minutes.Max() + 3) / 10d) * 10);
        var yStep = yMax <= 60 ? 10 : 20;

        DrawAxes(g, yMax, yStep);
        DrawScatter(g, samples, yMax);
        DrawAverageLine(g, samples, yMax);
        DrawNowMarker(g, yMax);
        DrawDayOfWeekPanel(g, samples);

        Directory.CreateDirectory(ChartDir);
        var path = Path.Combine(ChartDir, $"{routeId}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Averages per 30-minute bucket of the day, for the details body text.</summary>
    public static Stats Summarize(IReadOnlyList<JourneyHistoryStore.Sample> samples)
    {
        var buckets = BucketAverages(samples);
        var days = samples.Select(s => s.Time.Date).Distinct().Count();

        var best = buckets.MinBy(b => b.Average);
        var worst = buckets.MaxBy(b => b.Average);

        var now = DateTime.Now;
        var nowKey = TimeOfDayKey(now);
        var current = buckets.FirstOrDefault(b => b.Key == nowKey);

        var dayStats = DayStatistics(samples);
        var bestDay = dayStats.Where(d => d.Average is not null).MinBy(d => d.Average);
        var worstDay = dayStats.Where(d => d.Average is not null).MaxBy(d => d.Average);

        return new Stats(
            samples.Count,
            days,
            current is null ? null : (int)Math.Round(current.Average),
            best is null ? null : BucketLabel(best.Key),
            best is null ? null : (int)Math.Round(best.Average),
            worst is null ? null : BucketLabel(worst.Key),
            worst is null ? null : (int)Math.Round(worst.Average),
            dayStats,
            bestDay?.Label,
            bestDay?.Average,
            worstDay?.Label,
            worstDay?.Average);
    }

    private sealed record Bucket(int Key, double Average, int Count);

    private static List<Bucket> BucketAverages(IReadOnlyList<JourneyHistoryStore.Sample> samples) =>
        samples
            .GroupBy(s => TimeOfDayKey(s.Time))
            .Select(grp => new Bucket(grp.Key, grp.Average(s => s.Minutes), grp.Count()))
            .OrderBy(b => b.Key)
            .ToList();

    /// <summary>Bucket key = 30-minute slot index within [05:00, 23:00).</summary>
    private static int TimeOfDayKey(DateTime time)
    {
        var minuteOfDay = time.Hour * 60 + time.Minute;
        var clamped = Math.Clamp(minuteOfDay, DayStartMinutes, DayEndMinutes - 1);
        return (clamped - DayStartMinutes) / BucketMinutes;
    }

    private static string BucketLabel(int key)
    {
        var m = DayStartMinutes + key * BucketMinutes;
        return $"{m / 60:00}:{m % 60:00}";
    }

    private static void FillBoard(Graphics g)
    {
        g.Clear(Color.Transparent);
        using var path = RoundedRect(new RectangleF(0, 0, Width, Height), 28);
        using var bg = new SolidBrush(BoardBlack);
        g.FillPath(bg, path);

        // Title, SIEL-style unaccented caps in the dot-matrix face.
        using var title = new SolidBrush(AmberDim);
        using var titleFont = Font(26);
        g.DrawString("TEMPS DE TRAJET SELON L'HEURE", titleFont, title, 44, 34);
    }

    private static void DrawAxes(Graphics g, int yMax, int yStep)
    {
        using var gridPen = new Pen(AmberDim, 1) { DashStyle = DashStyle.Dot };
        using var labelBrush = new SolidBrush(AmberDim);
        using var labelFont = Font(18);

        // Horizontal grid + y labels (minutes).
        for (var m = 0; m <= yMax; m += yStep)
        {
            var y = Y(m, yMax);
            g.DrawLine(gridPen, Left, y, Right, y);
            g.DrawString($"{m}", labelFont, labelBrush, Left - 46, y - 10);
        }

        g.DrawString("MIN", labelFont, labelBrush, Left - 46, Top - 34);

        // Vertical grid + x labels (hours).
        for (var h = 6; h <= 22; h += 2)
        {
            var x = X(h * 60);
            g.DrawLine(gridPen, x, Top, x, Bottom);
            g.DrawString($"{h}H", labelFont, labelBrush, x - 14, Bottom + 16);
        }
    }

    private static void DrawScatter(Graphics g, IReadOnlyList<JourneyHistoryStore.Sample> samples, int yMax)
    {
        using var dot = new SolidBrush(Color.FromArgb(170, Amber));
        foreach (var sample in samples)
        {
            var minuteOfDay = sample.Time.Hour * 60 + sample.Time.Minute;
            if (minuteOfDay < DayStartMinutes || minuteOfDay > DayEndMinutes)
            {
                continue;
            }

            var x = X(minuteOfDay);
            var y = Y(sample.Minutes, yMax);
            g.FillEllipse(dot, x - 3, y - 3, 6, 6);
        }
    }

    private static void DrawAverageLine(Graphics g, IReadOnlyList<JourneyHistoryStore.Sample> samples, int yMax)
    {
        var points = BucketAverages(samples)
            .Select(b => new PointF(
                X(DayStartMinutes + b.Key * BucketMinutes + BucketMinutes / 2),
                Y((int)Math.Round(b.Average), yMax)))
            .ToArray();
        if (points.Length < 2)
        {
            return;
        }

        using var pen = new Pen(Amber, 5);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        g.DrawLines(pen, points);
    }

    private static void DrawNowMarker(Graphics g, int yMax)
    {
        var now = DateTime.Now;
        var minuteOfDay = now.Hour * 60 + now.Minute;
        if (minuteOfDay < DayStartMinutes || minuteOfDay > DayEndMinutes)
        {
            return;
        }

        var x = X(minuteOfDay);
        using var pen = new Pen(AmberDim, 2) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, x, Top, x, Bottom);

        using var labelBrush = new SolidBrush(AmberDim);
        using var labelFont = Font(18);
        g.DrawString("MAINT", labelFont, labelBrush, x - 30, Top - 34);
    }

    /// <summary>
    /// Second panel: average duration per day of week as amber bars — the
    /// "is Friday really worse than Tuesday" weekly comparison.
    /// </summary>
    private static void DrawDayOfWeekPanel(Graphics g, IReadOnlyList<JourneyHistoryStore.Sample> samples)
    {
        using var titleBrush = new SolidBrush(AmberDim);
        using var titleFont = Font(26);
        g.DrawString("TEMPS MOYEN PAR JOUR", titleFont, titleBrush, 44, WeekTitleY);

        var dayStats = DayStatistics(samples);
        var maxAvg = dayStats.Where(d => d.Average is not null).Select(d => d.Average!.Value).DefaultIfEmpty(0).Max();
        if (maxAvg <= 0)
        {
            return; // no per-day data (guarded by the 4-sample minimum, kept safe)
        }

        var yMaxDay = (int)(Math.Ceiling((maxAvg + 3) / 10d) * 10);

        // Baseline under the bars.
        using var axisPen = new Pen(AmberDim, 2);
        g.DrawLine(axisPen, Left, WeekBottom, Right, WeekBottom);

        var slot = (Right - Left) / 7f;
        var barWidth = slot * 0.58f;
        using var barBrush = new SolidBrush(Amber);
        using var dimBrush = new SolidBrush(AmberDim);
        using var labelFont = Font(18);

        for (var i = 0; i < dayStats.Count; i++)
        {
            var day = dayStats[i];
            var cx = Left + (slot * i) + (slot / 2);

            if (day.Average is { } avg)
            {
                var barHeight = avg * (WeekBottom - WeekTop) / yMaxDay;
                var barTop = WeekBottom - barHeight;
                using var bar = RoundedRect(new RectangleF(cx - (barWidth / 2), barTop, barWidth, barHeight), 8);
                g.FillPath(barBrush, bar);
                g.DrawString($"{avg}", labelFont, dimBrush, cx - 16, barTop - 30);
            }
            else
            {
                g.DrawString("--", labelFont, dimBrush, cx - 12, WeekBottom - 40);
            }

            g.DrawString(day.Label, labelFont, dimBrush, cx - 22, WeekLabelY);
        }
    }

    /// <summary>Per-day-of-week averages, Monday first (French, unaccented — the dot-matrix face has no accents).</summary>
    private static List<DayStat> DayStatistics(IReadOnlyList<JourneyHistoryStore.Sample> samples)
    {
        string[] labels = ["LUN", "MAR", "MER", "JEU", "VEN", "SAM", "DIM"];
        var byDay = samples
            .GroupBy(s => s.Time.DayOfWeek)
            .ToDictionary(g => g.Key, g => (Average: (int)Math.Round(g.Average(x => x.Minutes)), Count: g.Count()));

        return [.. Enumerable.Range(0, 7)
            .Select(i => new { DayOfWeek = (DayOfWeek)((i + 1) % 7), Label = labels[i] })
            .Select(d => byDay.TryGetValue(d.DayOfWeek, out var v)
                ? new DayStat(d.Label, v.Average, v.Count)
                : new DayStat(d.Label, null, 0))];
    }

    private static float X(int minuteOfDay) =>
        Left + (minuteOfDay - DayStartMinutes) * (Right - Left) / (DayEndMinutes - DayStartMinutes);

    private static float Y(int minutes, int yMax) => Bottom - minutes * (Bottom - Top) / yMax;

    private static Font Font(float size)
    {
        // Same dot-matrix face as the boards — reuse the renderer's loader.
        return RatpDisplayRenderer.ChartFont(size);
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
}
