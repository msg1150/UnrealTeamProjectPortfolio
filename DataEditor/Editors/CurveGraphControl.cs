using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JsonAssetDataEditor.Editors;

/** CurveTable 그래프의 한 Key Point다. */
public sealed record CurveGraphPoint(float Time, float Value);

/** 그래프에서 Key가 선택되었을 때 전달한다. */
public sealed class CurveGraphKeySelectedEventArgs(float time, float value) : EventArgs
{
    public float Time { get; } = time;
    public float Value { get; } = value;
}

/** 그래프에서 Key 드래그가 끝났을 때 전달한다. */
public sealed class CurveGraphKeyMoveCommittedEventArgs(
    float originalTime,
    float newTime,
    float newValue
) : EventArgs
{
    public float OriginalTime { get; } = originalTime;
    public float NewTime { get; } = newTime;
    public float NewValue { get; } = newValue;
}

/** 그래프 빈 공간을 더블클릭해 Key 추가를 요청했을 때 전달한다. */
public sealed class CurveGraphAddKeyRequestedEventArgs(
    float time,
    float value
) : EventArgs
{
    public float Time { get; } = time;
    public float Value { get; } = value;
}

/**
 * 외부 CurveTable Editor의 그래프 영역.
 *
 * 기능:
 * - Point 선택
 * - Point 직접 드래그(Time/Value)
 * - 빈 공간 더블클릭으로 Key 추가
 * - 마우스 휠 줌
 * - 가운데 마우스 드래그 Pan
 * - Linear / Constant / Cubic Auto Tangent 미리보기
 *
 * Cubic은 현재 JSON 규격에 개별 Tangent가 없으므로
 * 이 컨트롤에서 Auto Tangent를 계산해 시각화한다.
 */
public sealed class CurveGraphControl : FrameworkElement
{
    private const double LeftMargin = 64.0;
    private const double RightMargin = 22.0;
    private const double TopMargin = 20.0;
    private const double BottomMargin = 42.0;
    private const double PointRadius = 5.5;

    private readonly List<CurveGraphPoint> _points = [];
    private int _selectedIndex = -1;

    private bool _draggingPoint;
    private CurveGraphPoint? _dragOriginal;
    private CurveGraphPoint? _dragPreview;

    private bool _panning;
    private Point _lastPanPoint;

    private double _minTime = 0.0;
    private double _maxTime = 1.0;
    private double _minValue = 0.0;
    private double _maxValue = 1.0;

    public int InterpMode { get; set; }

    public event EventHandler<CurveGraphKeySelectedEventArgs>? KeySelected;
    public event EventHandler<CurveGraphKeyMoveCommittedEventArgs>? KeyMoveCommitted;
    public event EventHandler<CurveGraphAddKeyRequestedEventArgs>? AddKeyRequested;

    public CurveGraphControl()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        MouseLeftButtonDown += HandleLeftButtonDown;
        MouseLeftButtonUp += HandleLeftButtonUp;
        MouseMove += HandleMouseMove;
        MouseWheel += HandleMouseWheel;
        MouseDown += HandleMouseDown;
        MouseUp += HandleMouseUp;
    }

    public void SetPoints(
        IEnumerable<CurveGraphPoint> points,
        bool fitView
    )
    {
        var selectedTime =
            _selectedIndex >= 0 && _selectedIndex < _points.Count
                ? _points[_selectedIndex].Time
                : (float?)null;

        _points.Clear();
        _points.AddRange(points.OrderBy(x => x.Time));

        _selectedIndex = -1;

        if (selectedTime.HasValue)
        {
            for (var i = 0; i < _points.Count; i++)
            {
                if (NearlyEqual(_points[i].Time, selectedTime.Value))
                {
                    _selectedIndex = i;
                    break;
                }
            }
        }

        _draggingPoint = false;
        _dragOriginal = null;
        _dragPreview = null;

        if (fitView)
            FitView();
        else
            EnsureReasonableView();

        InvalidateVisual();
    }

    public void SelectTime(float time)
    {
        _selectedIndex = -1;

        for (var i = 0; i < _points.Count; i++)
        {
            if (!NearlyEqual(_points[i].Time, time))
                continue;

            _selectedIndex = i;
            break;
        }

        InvalidateVisual();
    }

    public void FitView()
    {
        if (_points.Count == 0)
        {
            _minTime = 0.0;
            _maxTime = 1.0;
            _minValue = 0.0;
            _maxValue = 1.0;
            InvalidateVisual();
            return;
        }

        var minT = _points.Min(x => (double)x.Time);
        var maxT = _points.Max(x => (double)x.Time);
        var minV = _points.Min(x => (double)x.Value);
        var maxV = _points.Max(x => (double)x.Value);

        if (Math.Abs(maxT - minT) < 1e-9)
        {
            minT -= 0.5;
            maxT += 0.5;
        }

        if (Math.Abs(maxV - minV) < 1e-9)
        {
            var padding = Math.Max(1.0, Math.Abs(minV) * 0.2);
            minV -= padding;
            maxV += padding;
        }

        var timePadding = (maxT - minT) * 0.12;
        var valuePadding = (maxV - minV) * 0.14;

        _minTime = minT - timePadding;
        _maxTime = maxT + timePadding;
        _minValue = minV - valuePadding;
        _maxValue = maxV + valuePadding;

        EnsureReasonableView();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(
            SystemColors.ControlLightLightBrush,
            new Pen(SystemColors.ControlDarkBrush, 1.0),
            bounds
        );

        var plot = GetPlotRect();
        if (plot.Width <= 1 || plot.Height <= 1)
            return;

        DrawGrid(dc, plot);
        DrawAxes(dc, plot);
        DrawCurve(dc, plot);
        DrawPoints(dc, plot);

        if (_points.Count == 0)
        {
            DrawText(
                dc,
                "Curve가 비어 있습니다. 그래프를 더블클릭하거나 + Key를 눌러 시작하세요.",
                new Point(plot.Left + 18, plot.Top + 18),
                SystemColors.GrayTextBrush,
                13
            );
        }
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        var gridPen = new Pen(
            new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)),
            1.0
        );

        const int divisions = 10;

        for (var i = 0; i <= divisions; i++)
        {
            var x = plot.Left + plot.Width * i / divisions;
            dc.DrawLine(
                gridPen,
                new Point(x, plot.Top),
                new Point(x, plot.Bottom)
            );

            var time = _minTime +
                       (_maxTime - _minTime) * i / divisions;

            DrawText(
                dc,
                FormatAxis(time),
                new Point(x - 18, plot.Bottom + 6),
                SystemColors.GrayTextBrush,
                10
            );
        }

        for (var i = 0; i <= divisions; i++)
        {
            var y = plot.Top + plot.Height * i / divisions;
            dc.DrawLine(
                gridPen,
                new Point(plot.Left, y),
                new Point(plot.Right, y)
            );

            var value = _maxValue -
                        (_maxValue - _minValue) * i / divisions;

            DrawText(
                dc,
                FormatAxis(value),
                new Point(5, y - 7),
                SystemColors.GrayTextBrush,
                10
            );
        }
    }

    private void DrawAxes(DrawingContext dc, Rect plot)
    {
        var axisPen = new Pen(SystemColors.ControlDarkDarkBrush, 1.0);

        if (_minValue <= 0.0 && _maxValue >= 0.0)
        {
            var y = DataToScreen(0.0, 0.0, plot).Y;
            dc.DrawLine(
                axisPen,
                new Point(plot.Left, y),
                new Point(plot.Right, y)
            );
        }

        if (_minTime <= 0.0 && _maxTime >= 0.0)
        {
            var x = DataToScreen(0.0, 0.0, plot).X;
            dc.DrawLine(
                axisPen,
                new Point(x, plot.Top),
                new Point(x, plot.Bottom)
            );
        }
    }

    private void DrawCurve(DrawingContext dc, Rect plot)
    {
        var points = GetDisplayPoints();
        if (points.Count < 2)
            return;

        var curvePen = new Pen(
            new SolidColorBrush(Color.FromRgb(38, 118, 196)),
            2.0
        );

        switch (InterpMode)
        {
            case 1:
                DrawConstantCurve(dc, plot, points, curvePen);
                break;

            case 2:
                DrawCubicCurve(dc, plot, points, curvePen);
                break;

            default:
                DrawLinearCurve(dc, plot, points, curvePen);
                break;
        }
    }

    private void DrawLinearCurve(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<CurveGraphPoint> points,
        Pen pen
    )
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            dc.DrawLine(
                pen,
                DataToScreen(points[i].Time, points[i].Value, plot),
                DataToScreen(points[i + 1].Time, points[i + 1].Value, plot)
            );
        }
    }

    private void DrawConstantCurve(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<CurveGraphPoint> points,
        Pen pen
    )
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = DataToScreen(
                points[i].Time,
                points[i].Value,
                plot
            );
            var horizontal = DataToScreen(
                points[i + 1].Time,
                points[i].Value,
                plot
            );
            var b = DataToScreen(
                points[i + 1].Time,
                points[i + 1].Value,
                plot
            );

            dc.DrawLine(pen, a, horizontal);
            dc.DrawLine(pen, horizontal, b);
        }
    }

    private void DrawCubicCurve(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<CurveGraphPoint> points,
        Pen pen
    )
    {
        const int samplesPerSegment = 28;

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];

            var dt = p1.Time - p0.Time;
            if (Math.Abs(dt) < 1e-8f)
                continue;

            var m0 = GetAutoSlope(points, i);
            var m1 = GetAutoSlope(points, i + 1);

            var previous = DataToScreen(
                p0.Time,
                p0.Value,
                plot
            );

            for (var sample = 1;
                 sample <= samplesPerSegment;
                 sample++)
            {
                var t = sample / (double)samplesPerSegment;

                var h00 = 2 * t * t * t - 3 * t * t + 1;
                var h10 = t * t * t - 2 * t * t + t;
                var h01 = -2 * t * t * t + 3 * t * t;
                var h11 = t * t * t - t * t;

                var time = p0.Time + dt * t;
                var value =
                    h00 * p0.Value +
                    h10 * dt * m0 +
                    h01 * p1.Value +
                    h11 * dt * m1;

                var current = DataToScreen(
                    time,
                    value,
                    plot
                );

                dc.DrawLine(pen, previous, current);
                previous = current;
            }
        }
    }

    private static double GetAutoSlope(
        IReadOnlyList<CurveGraphPoint> points,
        int index
    )
    {
        if (points.Count < 2)
            return 0.0;

        if (index <= 0)
        {
            var dt = points[1].Time - points[0].Time;
            return Math.Abs(dt) < 1e-8
                ? 0.0
                : (points[1].Value - points[0].Value) / dt;
        }

        if (index >= points.Count - 1)
        {
            var last = points.Count - 1;
            var dt = points[last].Time - points[last - 1].Time;
            return Math.Abs(dt) < 1e-8
                ? 0.0
                : (points[last].Value - points[last - 1].Value) / dt;
        }

        var totalDt = points[index + 1].Time -
                      points[index - 1].Time;

        return Math.Abs(totalDt) < 1e-8
            ? 0.0
            : (points[index + 1].Value -
               points[index - 1].Value) / totalDt;
    }

    private void DrawPoints(DrawingContext dc, Rect plot)
    {
        var points = GetDisplayPoints();

        for (var i = 0; i < points.Count; i++)
        {
            var screen = DataToScreen(
                points[i].Time,
                points[i].Value,
                plot
            );

            var isSelected = i == _selectedIndex;
            var radius = isSelected
                ? PointRadius + 2.0
                : PointRadius;

            var fill = isSelected
                ? new SolidColorBrush(Color.FromRgb(245, 164, 37))
                : new SolidColorBrush(Color.FromRgb(38, 118, 196));

            dc.DrawEllipse(
                fill,
                new Pen(SystemColors.ControlDarkDarkBrush, 1.0),
                screen,
                radius,
                radius
            );
        }
    }

    private List<CurveGraphPoint> GetDisplayPoints()
    {
        var result = _points
            .Select(x => new CurveGraphPoint(x.Time, x.Value))
            .ToList();

        if (_draggingPoint &&
            _dragPreview is not null &&
            _selectedIndex >= 0 &&
            _selectedIndex < result.Count)
        {
            result[_selectedIndex] = _dragPreview;
            result.Sort((a, b) => a.Time.CompareTo(b.Time));

            /*
             * 드래그 중에는 선택 Point가 인접 Key를 넘지 못하도록
             * Clamp하기 때문에 정렬 후 index도 사실상 유지된다.
             */
        }

        return result;
    }

    private void HandleLeftButtonDown(
        object sender,
        MouseButtonEventArgs e
    )
    {
        Focus();

        var mouse = e.GetPosition(this);
        var plot = GetPlotRect();

        if (!plot.Contains(mouse))
            return;

        var hit = HitTestPoint(mouse, plot);

        if (hit >= 0)
        {
            _selectedIndex = hit;
            var point = _points[hit];

            KeySelected?.Invoke(
                this,
                new CurveGraphKeySelectedEventArgs(
                    point.Time,
                    point.Value
                )
            );

            _draggingPoint = true;
            _dragOriginal = point;
            _dragPreview = point;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            var data = ScreenToData(mouse, plot);

            AddKeyRequested?.Invoke(
                this,
                new CurveGraphAddKeyRequestedEventArgs(
                    (float)data.Time,
                    (float)data.Value
                )
            );

            e.Handled = true;
        }
    }

    private void HandleLeftButtonUp(
        object sender,
        MouseButtonEventArgs e
    )
    {
        if (!_draggingPoint)
            return;

        ReleaseMouseCapture();
        _draggingPoint = false;

        if (_dragOriginal is not null &&
            _dragPreview is not null)
        {
            var original = _dragOriginal;
            var preview = _dragPreview;

            KeyMoveCommitted?.Invoke(
                this,
                new CurveGraphKeyMoveCommittedEventArgs(
                    original.Time,
                    preview.Time,
                    preview.Value
                )
            );
        }

        _dragOriginal = null;
        _dragPreview = null;
        InvalidateVisual();
        e.Handled = true;
    }

    private void HandleMouseMove(
        object sender,
        MouseEventArgs e
    )
    {
        var mouse = e.GetPosition(this);
        var plot = GetPlotRect();

        if (_draggingPoint &&
            _dragOriginal is not null &&
            _selectedIndex >= 0 &&
            _selectedIndex < _points.Count)
        {
            var data = ScreenToData(mouse, plot);
            var newTime = (float)data.Time;
            var newValue = (float)data.Value;

            /*
             * Key가 서로 교차하면 현재 선택 index와 Unreal Key 순서가
             * 불명확해지므로 이웃 Key 사이로 제한한다.
             */
            var epsilon = (float)Math.Max(
                (_maxTime - _minTime) * 0.0001,
                0.000001
            );

            if (_selectedIndex > 0)
            {
                newTime = Math.Max(
                    newTime,
                    _points[_selectedIndex - 1].Time + epsilon
                );
            }

            if (_selectedIndex < _points.Count - 1)
            {
                newTime = Math.Min(
                    newTime,
                    _points[_selectedIndex + 1].Time - epsilon
                );
            }

            _dragPreview = new CurveGraphPoint(
                newTime,
                newValue
            );

            KeySelected?.Invoke(
                this,
                new CurveGraphKeySelectedEventArgs(
                    newTime,
                    newValue
                )
            );

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_panning)
        {
            var delta = mouse - _lastPanPoint;
            _lastPanPoint = mouse;

            if (plot.Width > 0 && plot.Height > 0)
            {
                var timeShift =
                    -delta.X / plot.Width *
                    (_maxTime - _minTime);

                var valueShift =
                    delta.Y / plot.Height *
                    (_maxValue - _minValue);

                _minTime += timeShift;
                _maxTime += timeShift;
                _minValue += valueShift;
                _maxValue += valueShift;

                InvalidateVisual();
            }

            e.Handled = true;
        }
    }

    private void HandleMouseWheel(
        object sender,
        MouseWheelEventArgs e
    )
    {
        var plot = GetPlotRect();
        var mouse = e.GetPosition(this);

        if (!plot.Contains(mouse))
            return;

        var anchor = ScreenToData(mouse, plot);
        var factor = e.Delta > 0 ? 0.82 : 1.0 / 0.82;

        _minTime = anchor.Time +
                   (_minTime - anchor.Time) * factor;
        _maxTime = anchor.Time +
                   (_maxTime - anchor.Time) * factor;

        _minValue = anchor.Value +
                    (_minValue - anchor.Value) * factor;
        _maxValue = anchor.Value +
                    (_maxValue - anchor.Value) * factor;

        EnsureReasonableView();
        InvalidateVisual();
        e.Handled = true;
    }

    private void HandleMouseDown(
        object sender,
        MouseButtonEventArgs e
    )
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        _panning = true;
        _lastPanPoint = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void HandleMouseUp(
        object sender,
        MouseButtonEventArgs e
    )
    {
        if (e.ChangedButton != MouseButton.Middle ||
            !_panning)
        {
            return;
        }

        _panning = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private int HitTestPoint(Point mouse, Rect plot)
    {
        const double radius = 10.0;

        for (var i = 0; i < _points.Count; i++)
        {
            var screen = DataToScreen(
                _points[i].Time,
                _points[i].Value,
                plot
            );

            var dx = mouse.X - screen.X;
            var dy = mouse.Y - screen.Y;

            if (dx * dx + dy * dy <= radius * radius)
                return i;
        }

        return -1;
    }

    private Rect GetPlotRect()
    {
        var width = Math.Max(
            1.0,
            ActualWidth - LeftMargin - RightMargin
        );

        var height = Math.Max(
            1.0,
            ActualHeight - TopMargin - BottomMargin
        );

        return new Rect(
            LeftMargin,
            TopMargin,
            width,
            height
        );
    }

    private Point DataToScreen(
        double time,
        double value,
        Rect plot
    )
    {
        var x = plot.Left +
                (time - _minTime) /
                (_maxTime - _minTime) *
                plot.Width;

        var y = plot.Top +
                (_maxValue - value) /
                (_maxValue - _minValue) *
                plot.Height;

        return new Point(x, y);
    }

    private (double Time, double Value) ScreenToData(
        Point point,
        Rect plot
    )
    {
        var xRatio = (point.X - plot.Left) / plot.Width;
        var yRatio = (point.Y - plot.Top) / plot.Height;

        return (
            _minTime +
            xRatio * (_maxTime - _minTime),

            _maxValue -
            yRatio * (_maxValue - _minValue)
        );
    }

    private void EnsureReasonableView()
    {
        if (!double.IsFinite(_minTime) ||
            !double.IsFinite(_maxTime) ||
            Math.Abs(_maxTime - _minTime) < 1e-9)
        {
            _minTime = 0.0;
            _maxTime = 1.0;
        }

        if (!double.IsFinite(_minValue) ||
            !double.IsFinite(_maxValue) ||
            Math.Abs(_maxValue - _minValue) < 1e-9)
        {
            _minValue = 0.0;
            _maxValue = 1.0;
        }
    }

    private static string FormatAxis(double value)
    {
        if (Math.Abs(value) >= 10000 ||
            (Math.Abs(value) > 0 && Math.Abs(value) < 0.001))
        {
            return value.ToString(
                "0.###E+0",
                CultureInfo.InvariantCulture
            );
        }

        return value.ToString(
            "0.###",
            CultureInfo.InvariantCulture
        );
    }

    private void DrawText(
        DrawingContext dc,
        string text,
        Point point,
        Brush brush,
        double fontSize
    )
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );

        dc.DrawText(formatted, point);
    }

    private static bool NearlyEqual(float a, float b)
    {
        var scale = Math.Max(
            1.0f,
            Math.Max(Math.Abs(a), Math.Abs(b))
        );

        return Math.Abs(a - b) <= 1e-5f * scale;
    }
}
