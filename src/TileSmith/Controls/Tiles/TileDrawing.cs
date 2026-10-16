using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core;
using TileSmith.Core.Tiles;
using Windows.Foundation;
using Windows.UI;

namespace TileSmith.Controls.Tiles;

/// <summary>
/// The marks blocks draw with: the 35 M3 shapes, rings with a gap before the track, M3 Expressive's wavy progress
/// bar, bars and lines of a series, and pictures from the web or the disk. All vector, so they stay crisp at any scale.
/// </summary>
public static class TileDrawing
{
    // ── Shapes ───────────────────────────────────────────────────────────────────────

    /// <summary>The shape as geometry <paramref name="size"/> px square (the path data is in a 100 × 100 box: M, C and Z only).</summary>
    public static PathGeometry ShapeGeometry(TileShape shape, double size)
    {
        var geometry = new PathGeometry { Transform = new ScaleTransform { ScaleX = size / 100, ScaleY = size / 100 } };
        PathFigure? figure = null;
        var tokens = Tokenize(shape.PathData);
        var i = 0;
        char command = 'M';
        double Next() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) command = tokens[i++][0];
            switch (command)
            {
                case 'M':
                    figure = new PathFigure { StartPoint = new Point(Next(), Next()), IsClosed = true, IsFilled = true };
                    geometry.Figures.Add(figure);
                    command = 'L';
                    break;
                case 'L':
                    figure?.Segments.Add(new LineSegment { Point = new Point(Next(), Next()) });
                    break;
                case 'C':
                    figure?.Segments.Add(new BezierSegment { Point1 = new Point(Next(), Next()), Point2 = new Point(Next(), Next()), Point3 = new Point(Next(), Next()) });
                    break;
                case 'Z' or 'z':
                    figure = null;
                    break;
                default:
                    i++;
                    break;
            }
        }
        return geometry;
    }

    private static List<string> Tokenize(string data)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (sb.Length > 0) list.Add(sb.ToString());
            sb.Clear();
        }
        foreach (var c in data)
        {
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                Flush();
                list.Add(c.ToString());
            }
            else if (c is ',' or ' ')
            {
                Flush();
            }
            else if (c == '-' && sb.Length > 0 && sb[^1] is not ('e' or 'E'))
            {
                Flush();
                sb.Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        Flush();
        return list;
    }

    public static Microsoft.UI.Xaml.Shapes.Path Shape(TileShape shape, double size, Color fill) => new()
    {
        Data = ShapeGeometry(shape, size),
        Fill = Theme.Palette.Current.Brush(fill),
        Width = size,
        Height = size,
        IsHitTestVisible = false,
    };

    // ── Rings ────────────────────────────────────────────────────────────────────────

    public static double RingStroke(double d) => Math.Clamp(d * 0.105, 4, 11);

    /// <summary>A progress ring <paramref name="d"/> px across: the indicator from the top, clockwise, a gap, then the track.</summary>
    public static Grid Ring(double d, double fraction, Color indicator, Color track)
    {
        var grid = new Grid { Width = d, Height = d, IsHitTestVisible = false };
        var stroke = RingStroke(d);
        var r = (d - stroke) / 2;
        var f = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        // the gap is a stroke's width plus a little, in degrees at this radius
        var gap = (stroke + 3) / r * 180 / Math.PI;
        if (f <= 0.001)
        {
            grid.Children.Add(Circle(d, stroke, track));
            return grid;
        }
        if (f >= 0.999)
        {
            grid.Children.Add(Circle(d, stroke, indicator));
            return grid;
        }
        var sweep = Math.Max(f * 360, 0.5);
        var trackStart = sweep + gap;
        var trackEnd = 360 - gap;
        if (trackEnd - trackStart > 1) grid.Children.Add(Arc(d, stroke, trackStart, trackEnd, track));
        grid.Children.Add(Arc(d, stroke, 0, sweep, indicator));
        return grid;
    }

    private static Ellipse Circle(double d, double stroke, Color color) => new()
    {
        Width = d,
        Height = d,
        StrokeThickness = stroke,
        Stroke = Theme.Palette.Current.Brush(color),
        IsHitTestVisible = false,
    };

    private static Microsoft.UI.Xaml.Shapes.Path Arc(double d, double stroke, double fromDeg, double toDeg, Color color)
    {
        var r = (d - stroke) / 2;
        var c = d / 2;
        Point At(double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
        }
        var figure = new PathFigure { StartPoint = At(fromDeg), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = At(toDeg),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = toDeg - fromDeg > 180,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = Theme.Palette.Current.Brush(color),
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = d,
            Height = d,
            IsHitTestVisible = false,
        };
    }

    // ── The wavy bar ─────────────────────────────────────────────────────────────────

    /// <summary>M3 Expressive's progress bar: a wave up to the value, a gap, a flat track and a stop dot at the end.</summary>
    public static Canvas WavyBar(double w, double h, double fraction, Color indicator, Color track, bool wavy = true)
    {
        var canvas = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        var t = Math.Clamp(h * 0.42, 3, 6);
        var mid = h / 2;
        var f = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var gap = t + 3;
        var end = t / 2 + f * (w - t);
        var amplitude = wavy ? Math.Max(0, (h - t) / 2 - 0.5) : 0;
        var wavelength = Math.Max(18, h * 2.4);
        var brush = Theme.Palette.Current;

        // track
        var trackFrom = f <= 0.001 ? t / 2 : end + gap;
        if (trackFrom < w - t / 2)
        {
            canvas.Children.Add(new Line
            {
                X1 = trackFrom,
                Y1 = mid,
                X2 = w - t / 2,
                Y2 = mid,
                Stroke = brush.Brush(track),
                StrokeThickness = t,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
            // the stop indicator
            var dot = new Ellipse { Width = t * 0.8, Height = t * 0.8, Fill = brush.Brush(indicator) };
            Canvas.SetLeft(dot, w - t / 2 - t * 0.4);
            Canvas.SetTop(dot, mid - t * 0.4);
            canvas.Children.Add(dot);
        }
        if (f > 0.001)
        {
            var line = new Polyline
            {
                Stroke = brush.Brush(indicator),
                StrokeThickness = t,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var steps = Math.Max(2, (int)Math.Ceiling((end - t / 2) / 2));
            for (var s = 0; s <= steps; s++)
            {
                var x = t / 2 + (end - t / 2) * s / steps;
                // the wave flattens near the start so the round cap sits on the line
                var damp = Math.Min(1, (x - t / 2) / Math.Max(1, wavelength / 2));
                line.Points.Add(new Point(x, mid + amplitude * damp * Math.Sin(2 * Math.PI * (x - t / 2) / wavelength)));
            }
            canvas.Children.Add(line);
        }
        return canvas;
    }

    // ── Charts ───────────────────────────────────────────────────────────────────────

    /// <summary>Bars of a series, the newest in the accent; or a line with a soft fill under it.</summary>
    public static Grid Chart(double w, double h, IReadOnlyList<double> series, bool line, Color accent, Color track)
    {
        var grid = new Grid { Width = w, Height = h, IsHitTestVisible = false };
        var values = series.Where(double.IsFinite).ToList();
        if (values.Count < 2) return grid;
        var min = values.Min();
        var max = values.Max();
        if (!line) min = Math.Min(0, min);
        var range = Math.Max(1e-9, max - min);
        var p = Theme.Palette.Current;
        if (line)
        {
            var pad = 2.5;
            var pts = new List<Point>();
            for (var i = 0; i < series.Count; i++)
            {
                if (!double.IsFinite(series[i])) continue;
                var x = series.Count == 1 ? w / 2 : i * (w - 2 * pad) / (series.Count - 1) + pad;
                var y = pad + (h - 2 * pad) * (1 - (series[i] - min) / range);
                pts.Add(new Point(x, y));
            }
            var area = new PathFigure { StartPoint = new Point(pts[0].X, h), IsClosed = true, IsFilled = true };
            foreach (var pt in pts) area.Segments.Add(new LineSegment { Point = pt });
            area.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, h) });
            var areaGeometry = new PathGeometry();
            areaGeometry.Figures.Add(area);
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fill.GradientStops.Add(new GradientStop { Color = Theme.Palette.WithAlpha(accent, 0.28), Offset = 0 });
            fill.GradientStops.Add(new GradientStop { Color = Theme.Palette.WithAlpha(accent, 0.02), Offset = 1 });
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = areaGeometry, Fill = fill });
            var polyline = new Polyline { Stroke = p.Brush(accent), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            foreach (var pt in pts) polyline.Points.Add(pt);
            grid.Children.Add(polyline);
            var dot = new Ellipse { Width = 6, Height = 6, Fill = p.Brush(accent), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(pts[^1].X - 3, pts[^1].Y - 3, 0, 0) };
            grid.Children.Add(dot);
            return grid;
        }
        var count = series.Count;
        var gapRatio = count > 24 ? 0.25 : 0.32;
        var bar = w / (count + (count - 1) * gapRatio);
        var gap = bar * gapRatio;
        for (var i = 0; i < count; i++)
        {
            var v = double.IsFinite(series[i]) ? series[i] : min;
            var bh = Math.Max(Math.Min(bar, 3), (h) * (v - min) / range);
            var rect = new Rectangle
            {
                Width = bar,
                Height = bh,
                RadiusX = Math.Min(bar / 2, 4),
                RadiusY = Math.Min(bar / 2, 4),
                Fill = p.Brush(i == count - 1 ? accent : track),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i * (bar + gap), 0, 0, 0),
            };
            grid.Children.Add(rect);
        }
        return grid;
    }

    // ── Pictures ─────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, WeakReference<BitmapImage>> Bitmaps = [];

    /// <summary>A picture from a web address, a file or a data URI, decoded at about <paramref name="decodeWidth"/> px wide.</summary>
    public static async Task<ImageSource?> LoadAsync(string? src, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        var key = src + "|" + decodeWidth;
        if (Bitmaps.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached)) return cached;
        try
        {
            BitmapImage bitmap;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(src) };
            }
            else
            {
                byte[] bytes;
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]);
                }
                else
                {
                    var path = Environment.ExpandEnvironmentVariables(src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : src.Trim('"'));
                    if (!File.Exists(path)) return null;
                    bytes = await File.ReadAllBytesAsync(path);
                }
                bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
                using var stream = new MemoryStream(bytes);
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            }
            Bitmaps[key] = new WeakReference<BitmapImage>(bitmap);
            if (Bitmaps.Count > 300)
            {
                foreach (var dead in Bitmaps.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList()) Bitmaps.Remove(dead);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"Picture couldn't load ({src[..Math.Min(src.Length, 80)]}): {ex.Message}");
            return null;
        }
    }

    /// <summary>A rounded picture <paramref name="w"/> × <paramref name="h"/>, or a quiet tile with an icon until it is in.</summary>
    public static Grid Picture(string? src, double w, double h, double radius, Color placeholder, Color icon, string iconName, bool fit = false)
    {
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        var host = new Grid { Width = w, Height = h, CornerRadius = new CornerRadius(r), Background = Theme.Palette.Current.Brush(placeholder), IsHitTestVisible = false };
        var glyph = Glyph.Make(iconName, Math.Clamp(Math.Min(w, h) * 0.42, 10, 28), icon);
        host.Children.Add(glyph);
        if (string.IsNullOrWhiteSpace(src)) return host;
        var image = new Image { Stretch = fit ? Stretch.Uniform : Stretch.UniformToFill, Width = w, Height = h, Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) } };
        host.Children.Add(image);
        _ = Load();
        return host;

        async Task Load()
        {
            var source = await LoadAsync(src, (int)Math.Ceiling(w * 1.5));
            if (source is null) return;
            image.Source = source;
            image.Opacity = 1;
            glyph.Visibility = Visibility.Collapsed;
        }
    }
}
