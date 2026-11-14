using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Marsianskiy.Controls;

public partial class EarthGlobeView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty NodeNameProperty = DependencyProperty.Register(nameof(NodeName), typeof(string), typeof(EarthGlobeView), new PropertyMetadata("marsianskiy"));
    public static readonly DependencyProperty NodeDetailsProperty = DependencyProperty.Register(nameof(NodeDetails), typeof(string), typeof(EarthGlobeView), new PropertyMetadata("Локальные серверы · split routing"));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(EarthGlobeView), new PropertyMetadata(false, (sender, _) => ((EarthGlobeView)sender).UpdateAnimation()));

    private static readonly SolidColorBrush[] LandPalette = CreatePalette(0xD8, 0xD2, 0xF3);
    private static readonly SolidColorBrush[] GridPalette = CreatePalette(0xA7, 0x9C, 0xD0);
    private static readonly SolidColorBrush SphereBrush = CreateBrush(0x54, 0x45, 0x8A, 14);
    private static readonly SolidColorBrush MarkerGlowBrush = CreateBrush(0xF4, 0xB7, 0x7B, 45);
    private static readonly SolidColorBrush MarkerBrush = CreateBrush(0xF4, 0xB7, 0x7B, 255);

    private readonly List<GlobePoint> _points = [];
    private readonly List<StarPoint> _stars = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(34) };
    private readonly DateTime _started = DateTime.UtcNow;
    private double _parallaxX;
    private double _parallaxY;

    public EarthGlobeView()
    {
        InitializeComponent();
        BuildSphere();
        BuildStars();
        _timer.Tick += (_, _) => DrawGlobe();
        Loaded += (_, _) => { UpdateAnimation(); DrawGlobe(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        SizeChanged += (_, _) => DrawGlobe();
        MouseMove += (_, args) =>
        {
            var position = args.GetPosition(this);
            _parallaxX = (position.X / Math.Max(1, ActualWidth) - 0.5) * 16;
            _parallaxY = (position.Y / Math.Max(1, ActualHeight) - 0.5) * 12;
        };
        MouseLeave += (_, _) => { _parallaxX = 0; _parallaxY = 0; };
    }

    private void BuildStars()
    {
        var random = new Random(42);
        for (var i = 0; i < 46; i++) _stars.Add(new StarPoint(random.NextDouble(), random.NextDouble(), 0.5 + random.NextDouble(), random.NextDouble() * Math.PI * 2));
    }

    public string NodeName { get => (string)GetValue(NodeNameProperty); set => SetValue(NodeNameProperty, value); }
    public string NodeDetails { get => (string)GetValue(NodeDetailsProperty); set => SetValue(NodeDetailsProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    private void UpdateAnimation()
    {
        if (IsLoaded && IsVisible && !ReduceMotion) _timer.Start();
        else _timer.Stop();
    }

    private void BuildSphere()
    {
        for (var lat = -75; lat <= 75; lat += 3)
        for (var lon = -180; lon < 180; lon += 3)
        {
            var land = IsLand(lon, lat);
            if (!land && (lat % 6 != 0 || lon % 6 != 0)) continue;
            var latitude = lat * Math.PI / 180;
            var longitude = lon * Math.PI / 180;
            var cosLat = Math.Cos(latitude);
            _points.Add(new GlobePoint(Math.Sin(latitude), cosLat * Math.Sin(longitude), cosLat * Math.Cos(longitude), land));
        }
    }

    private void DrawGlobe()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;
        var time = (DateTime.UtcNow - _started).TotalSeconds;
        var rotation = time * 0.16;
        var sinRotation = Math.Sin(rotation);
        var cosRotation = Math.Cos(rotation);
        const double tilt = -0.24;
        var sinTilt = Math.Sin(tilt);
        var cosTilt = Math.Cos(tilt);
        var center = new Point(width / 2 + _parallaxX, height / 2 - 8 + _parallaxY);
        var radius = Math.Min(width, height) * 0.39;
        Host.Draw(context =>
        {
            foreach (var star in _stars)
            {
                var alpha = (byte)(35 + 45 * (1 + Math.Sin(time * 1.4 + star.Phase)) / 2);
                context.DrawEllipse(GridPalette[alpha], null, new Point(star.X * width, star.Y * height), star.Size, star.Size);
            }
            var outerPen = new Pen(CreateBrush(0x9D, 0x8B, 0xD7, 50), 1);
            outerPen.Freeze();
            context.DrawEllipse(null, outerPen, center, radius + 13, radius + 13);
            context.DrawEllipse(SphereBrush, null, center, radius + 6, radius + 6);
            foreach (var point in _points)
            {
                var x = point.BaseX * cosRotation + point.BaseZ * sinRotation;
                var z = point.BaseZ * cosRotation - point.BaseX * sinRotation;
                var tiltedY = point.SinLatitude * cosTilt - z * sinTilt;
                var tiltedZ = point.SinLatitude * sinTilt + z * cosTilt;
                if (tiltedZ < 0.03) continue;
                var screen = new Point(center.X + radius * x, center.Y - radius * tiltedY);
                var brightness = (byte)(point.IsLand ? 115 + 125 * tiltedZ : 60 + 55 * tiltedZ);
                var brush = point.IsLand ? LandPalette[brightness] : GridPalette[brightness];
                var dot = (point.IsLand ? 0.85 : 0.55) + tiltedZ * 0.45;
                context.DrawEllipse(brush, null, screen, dot, dot);
            }
            var markerAngle = rotation * 0.35 + 0.7;
            var markerX = center.X + Math.Cos(markerAngle) * radius * 0.72;
            var markerY = center.Y - radius * 0.3 + Math.Sin(markerAngle) * 8;
            context.DrawEllipse(MarkerGlowBrush, null, new Point(markerX, markerY), 9, 9);
            context.DrawEllipse(MarkerBrush, null, new Point(markerX, markerY), 3.2, 3.2);
            for (var satellite = 0; satellite < 2; satellite++)
            {
                var angle = time * (satellite == 0 ? 0.65 : -0.45) + satellite * Math.PI;
                var position = new Point(center.X + Math.Cos(angle) * radius * 1.16, center.Y + Math.Sin(angle) * radius * 0.34);
                context.DrawEllipse(MarkerGlowBrush, null, position, 7, 7);
                context.DrawEllipse(MarkerBrush, null, position, 2.2, 2.2);
            }
        });
    }

    private static SolidColorBrush[] CreatePalette(byte r, byte g, byte b)
    {
        var palette = new SolidColorBrush[256];
        for (var alpha = 0; alpha < 256; alpha++) palette[alpha] = CreateBrush(r, g, b, (byte)alpha);
        return palette;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static bool IsLand(double longitude, double latitude) => Continents.Any(polygon => InsidePolygon(longitude, latitude, polygon));

    private static bool InsidePolygon(double x, double y, (double Lon, double Lat)[] polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Lat > y) != (b.Lat > y) && x < (b.Lon - a.Lon) * (y - a.Lat) / (b.Lat - a.Lat) + a.Lon) inside = !inside;
        }
        return inside;
    }

    private static readonly (double Lon, double Lat)[][] Continents =
    [
        [(-168, 70), (-140, 72), (-125, 55), (-130, 45), (-122, 30), (-105, 20), (-97, 15), (-85, 20), (-80, 27), (-65, 45), (-75, 55), (-95, 60), (-110, 72)],
        [(-80, 12), (-60, 8), (-45, -5), (-35, -15), (-50, -35), (-65, -55), (-75, -35), (-80, -10)],
        [(-10, 35), (-10, 58), (10, 72), (40, 70), (60, 77), (120, 72), (170, 60), (160, 45), (130, 35), (120, 20), (100, 5), (75, 8), (60, 25), (40, 30), (30, 40), (15, 35)],
        [(-17, 15), (10, 37), (35, 30), (50, 10), (42, -15), (28, -35), (12, -35), (0, -20), (-10, 0)],
        [(112, -11), (153, -10), (154, -30), (135, -44), (113, -30)],
        [(-55, 60), (-20, 70), (-35, 83), (-65, 78)]
    ];

    private sealed record GlobePoint(double SinLatitude, double BaseX, double BaseZ, bool IsLand);
    private sealed record StarPoint(double X, double Y, double Size, double Phase);
}

public sealed class GlobeVisualHost : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public GlobeVisualHost() => AddVisualChild(_visual);
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    public void Draw(Action<DrawingContext> drawing)
    {
        using var context = _visual.RenderOpen();
        drawing(context);
    }
}
