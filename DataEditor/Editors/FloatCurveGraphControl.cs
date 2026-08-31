using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JsonAssetDataEditor.Editors;

public sealed class FloatCurveKeySelectedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

public sealed class FloatCurveKeyMoveCommittedEventArgs(
    int index,
    float newTime,
    float newValue
) : EventArgs
{
    public int Index { get; } = index;
    public float NewTime { get; } = newTime;
    public float NewValue { get; } = newValue;
}

public sealed class FloatCurveTangentMoveCommittedEventArgs(
    int index,
    bool isArrive,
    float tangent,
    float tangentWeight
) : EventArgs
{
    public int Index { get; } = index;
    public bool IsArrive { get; } = isArrive;
    public float Tangent { get; } = tangent;

    /**
     * Weighted Tangent일 때 Unreal의 Arrive/LeaveTangentWeight에
     * 그대로 적용할 Handle 길이다. Unweighted면 Editor에서 무시한다.
     */
    public float TangentWeight { get; } = tangentWeight;
}

public sealed class FloatCurveAddKeyRequestedEventArgs(
    float time,
    float value
) : EventArgs
{
    public float Time { get; } = time;
    public float Value { get; } = value;
}

/**
 * UCurveFloat 전용 그래프 컨트롤.
 *
 * Unreal Curve Editor의 핵심 조작을 외부 프로그램에서 재현한다.
 * - Key 선택 / Time-Value Drag
 * - Cubic Tangent Handle 표시 / Drag
 * - Constant / Linear / Cubic 시각화
 * - Wheel Zoom
 * - MMB 또는 RMB Drag Pan
 * - 빈 공간 Double Click Key 추가
 */
public sealed class FloatCurveGraphControl : FrameworkElement
{
    private const double LeftMargin = 70;
    private const double RightMargin = 24;
    private const double TopMargin = 24;
    private const double BottomMargin = 46;
    private const double KeyRadius = 6;
    private const double HitRadius = 11;
    private const double TangentHandleRadius = 5;

    private readonly List<FloatCurveKeyDocument> _keys = [];
    private int _selectedIndex = -1;

    private bool _draggingKey;
    private int _dragKeyIndex = -1;
    private FloatCurveKeyDocument? _dragPreview;

    private bool _draggingTangent;
    private bool _draggingArriveTangent;
    private int _dragTangentIndex = -1;
    private float _dragTangentPreview;
    private float _dragTangentWeightPreview;

    private bool _panning;
    private Point _lastPanPoint;

    private double _minTime = 0;
    private double _maxTime = 1;
    private double _minValue = 0;
    private double _maxValue = 1;

    public event EventHandler<FloatCurveKeySelectedEventArgs>? KeySelected;
    public event EventHandler<FloatCurveKeyMoveCommittedEventArgs>? KeyMoveCommitted;
    public event EventHandler<FloatCurveTangentMoveCommittedEventArgs>? TangentMoveCommitted;
    public event EventHandler<FloatCurveAddKeyRequestedEventArgs>? AddKeyRequested;

    public FloatCurveGraphControl()
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

    public void SetCurve(
        IReadOnlyList<FloatCurveKeyDocument> keys,
        int selectedIndex,
        bool fitView
    )
    {
        _keys.Clear();
        _keys.AddRange(keys.Select(CloneKey));
        _selectedIndex =
            selectedIndex >= 0 && selectedIndex < _keys.Count
                ? selectedIndex
                : -1;

        CancelDrag();

        if (fitView)
            FitView();
        else
            EnsureReasonableView();

        InvalidateVisual();
    }

    public void FitView()
    {
        if (_keys.Count == 0)
        {
            _minTime = 0;
            _maxTime = 1;
            _minValue = 0;
            _maxValue = 1;
            InvalidateVisual();
            return;
        }

        var minT = _keys.Min(x => (double)x.Time);
        var maxT = _keys.Max(x => (double)x.Time);
        var minV = _keys.Min(x => (double)x.Value);
        var maxV = _keys.Max(x => (double)x.Value);

        if (Math.Abs(maxT - minT) < 1e-9)
        {
            minT -= 0.5;
            maxT += 0.5;
        }

        if (Math.Abs(maxV - minV) < 1e-9)
        {
            var p = Math.Max(1.0, Math.Abs(minV) * 0.25);
            minV -= p;
            maxV += p;
        }

        var timePadding = (maxT - minT) * 0.12;
        var valuePadding = (maxV - minV) * 0.15;

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
            SystemColors.WindowBrush,
            new Pen(SystemColors.ControlDarkBrush, 1),
            bounds
        );

        var plot = GetPlotRect();
        if (plot.Width <= 1 || plot.Height <= 1)
            return;

        DrawGrid(dc, plot);
        DrawAxes(dc, plot);
        DrawCurve(dc, plot);
        DrawKeys(dc, plot);
        DrawSelectedTangents(dc, plot);

        if (_keys.Count == 0)
        {
            DrawText(
                dc,
                "Float Curve가 비어 있습니다. 그래프를 더블클릭하거나 + Key를 눌러 Key를 추가하세요.",
                new Point(plot.Left + 18, plot.Top + 18),
                SystemColors.GrayTextBrush,
                13
            );
        }
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        var gridPen = new Pen(
            new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)),
            1
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

            var time =
                _minTime +
                (_maxTime - _minTime) * i / divisions;

            DrawText(
                dc,
                FormatAxis(time),
                new Point(x - 18, plot.Bottom + 7),
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

            var value =
                _maxValue -
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
        var axisPen = new Pen(
            SystemColors.ControlDarkDarkBrush,
            1.1
        );

        if (_minValue <= 0 && _maxValue >= 0)
        {
            var y = DataToScreen(0, 0, plot).Y;
            dc.DrawLine(
                axisPen,
                new Point(plot.Left, y),
                new Point(plot.Right, y)
            );
        }

        if (_minTime <= 0 && _maxTime >= 0)
        {
            var x = DataToScreen(0, 0, plot).X;
            dc.DrawLine(
                axisPen,
                new Point(x, plot.Top),
                new Point(x, plot.Bottom)
            );
        }
    }

    private void DrawCurve(DrawingContext dc, Rect plot)
    {
        var display = GetDisplayKeys();
        if (display.Count < 2)
            return;

        var curvePen = new Pen(
            new SolidColorBrush(Color.FromRgb(62, 150, 65)),
            2.1
        );

        for (var i = 0; i < display.Count - 1; i++)
        {
            var a = display[i];
            var b = display[i + 1];

            if (IsConstant(a.InterpMode))
            {
                var screenA = DataToScreen(a.Time, a.Value, plot);
                var horizontal = DataToScreen(b.Time, a.Value, plot);
                var screenB = DataToScreen(b.Time, b.Value, plot);

                dc.DrawLine(curvePen, screenA, horizontal);
                dc.DrawLine(curvePen, horizontal, screenB);
            }
            else if (IsCubic(a.InterpMode))
            {
                DrawCubicSegment(dc, plot, a, b, curvePen);
            }
            else
            {
                dc.DrawLine(
                    curvePen,
                    DataToScreen(a.Time, a.Value, plot),
                    DataToScreen(b.Time, b.Value, plot)
                );
            }
        }
    }

    private void DrawCubicSegment(
        DrawingContext dc,
        Rect plot,
        FloatCurveKeyDocument a,
        FloatCurveKeyDocument b,
        Pen pen
    )
    {
        if (Math.Abs(b.Time - a.Time) < 1e-8f)
            return;

        /*
         * Unreal FRichCurve의 Cubic은 TangentWeightMode에 따라
         * 일반 Hermite 또는 Weighted Bezier 경로로 평가된다.
         *
         * 일반 Hermite도 Bezier Control Point로 정확히 변환할 수 있으므로
         * 두 경우 모두 동일한 Control Point 표현으로 그린다.
         * Weighted Tangent이면 JSON의 실제 Weight를 사용하고,
         * Unweighted Tangent이면 Hermite와 같은 dt/3 길이로 변환한다.
         */
        GetBezierControlPoints(
            a,
            b,
            out var p0,
            out var p1,
            out var p2,
            out var p3
        );

        const int samples = 72;
        var previous = DataToScreen(p0.X, p0.Y, plot);

        for (var i = 1; i <= samples; i++)
        {
            var u = i / (double)samples;
            var oneMinusU = 1.0 - u;

            var b0 = oneMinusU * oneMinusU * oneMinusU;
            var b1 = 3.0 * oneMinusU * oneMinusU * u;
            var b2 = 3.0 * oneMinusU * u * u;
            var b3 = u * u * u;

            var time =
                b0 * p0.X +
                b1 * p1.X +
                b2 * p2.X +
                b3 * p3.X;

            var value =
                b0 * p0.Y +
                b1 * p1.Y +
                b2 * p2.Y +
                b3 * p3.Y;

            var current = DataToScreen(time, value, plot);
            dc.DrawLine(pen, previous, current);
            previous = current;
        }
    }

    /**
     * FRichCurve Cubic Segment를 Bezier Control Point 4개로 변환한다.
     *
     * Tangent는 dValue/dTime 기울기이고 Weight는 Time/Value 평면에서
     * Tangent Handle까지의 실제 길이로 취급한다. Weighted가 아닌 쪽은
     * 일반 Cubic Hermite와 동일하도록 Segment Time의 1/3을 사용한다.
     */
    private static void GetBezierControlPoints(
        FloatCurveKeyDocument a,
        FloatCurveKeyDocument b,
        out Point p0,
        out Point p1,
        out Point p2,
        out Point p3
    )
    {
        var dt = Math.Max(1e-8, (double)b.Time - a.Time);

        var leaveWeight = IsLeaveWeighted(a)
            ? Math.Max(0.0, a.LeaveTangentWeight)
            : GetDefaultTangentWeight(dt, a.LeaveTangent);

        var arriveWeight = IsArriveWeighted(b)
            ? Math.Max(0.0, b.ArriveTangentWeight)
            : GetDefaultTangentWeight(dt, b.ArriveTangent);

        var leaveDir = GetNormalizedTangentDirection(a.LeaveTangent);
        var arriveDir = GetNormalizedTangentDirection(b.ArriveTangent);

        p0 = new Point(a.Time, a.Value);
        p1 = new Point(
            a.Time + leaveDir.X * leaveWeight,
            a.Value + leaveDir.Y * leaveWeight
        );

        p3 = new Point(b.Time, b.Value);
        p2 = new Point(
            b.Time - arriveDir.X * arriveWeight,
            b.Value - arriveDir.Y * arriveWeight
        );
    }

    /** Hermite dt/3 Control Point를 Tangent Weight 길이로 변환한다. */
    private static double GetDefaultTangentWeight(
        double segmentTime,
        double tangent
    )
    {
        var x = segmentTime / 3.0;
        var y = tangent * x;
        return Math.Sqrt(x * x + y * y);
    }

    /** 기울기 dValue/dTime을 Time/Value 평면의 단위 방향 벡터로 바꾼다. */
    private static Point GetNormalizedTangentDirection(double tangent)
    {
        var length = Math.Sqrt(1.0 + tangent * tangent);
        if (!double.IsFinite(length) || length <= 1e-12)
            return new Point(1.0, 0.0);

        return new Point(1.0 / length, tangent / length);
    }

    private void DrawKeys(DrawingContext dc, Rect plot)
    {
        var display = GetDisplayKeys();

        for (var i = 0; i < display.Count; i++)
        {
            var screen = DataToScreen(
                display[i].Time,
                display[i].Value,
                plot
            );

            var selected = i == _selectedIndex;
            var fill = selected
                ? new SolidColorBrush(Color.FromRgb(245, 163, 35))
                : new SolidColorBrush(Color.FromRgb(62, 150, 65));

            dc.DrawRectangle(
                fill,
                new Pen(SystemColors.ControlDarkDarkBrush, 1),
                new Rect(
                    screen.X - KeyRadius,
                    screen.Y - KeyRadius,
                    KeyRadius * 2,
                    KeyRadius * 2
                )
            );
        }
    }

    private void DrawSelectedTangents(
        DrawingContext dc,
        Rect plot
    )
    {
        if (_selectedIndex < 0 ||
            _selectedIndex >= _keys.Count)
        {
            return;
        }

        var key =
            _draggingKey &&
            _dragKeyIndex == _selectedIndex &&
            _dragPreview is not null
                ? _dragPreview
                : _keys[_selectedIndex];

        if (!IsCubic(key.InterpMode))
            return;

        var keyScreen = DataToScreen(
            key.Time,
            key.Value,
            plot
        );

        var linePen = new Pen(
            new SolidColorBrush(Color.FromRgb(226, 133, 36)),
            1.3
        );

        var arrive = GetTangentHandlePoint(
            _selectedIndex,
            isArrive: true,
            plot
        );
        var leave = GetTangentHandlePoint(
            _selectedIndex,
            isArrive: false,
            plot
        );

        dc.DrawLine(linePen, keyScreen, arrive);
        dc.DrawLine(linePen, keyScreen, leave);

        var handleBrush =
            new SolidColorBrush(Color.FromRgb(250, 197, 80));

        dc.DrawEllipse(
            handleBrush,
            new Pen(SystemColors.ControlDarkDarkBrush, 1),
            arrive,
            TangentHandleRadius,
            TangentHandleRadius
        );
        dc.DrawEllipse(
            handleBrush,
            new Pen(SystemColors.ControlDarkDarkBrush, 1),
            leave,
            TangentHandleRadius,
            TangentHandleRadius
        );
    }

    private Point GetTangentHandlePoint(
        int index,
        bool isArrive,
        Rect plot
    )
    {
        var key = _keys[index];
        var tangent = isArrive
            ? key.ArriveTangent
            : key.LeaveTangent;

        var weight = GetDisplayTangentWeight(index, isArrive, tangent);

        if (_draggingTangent &&
            _dragTangentIndex == index &&
            _draggingArriveTangent == isArrive)
        {
            tangent = _dragTangentPreview;
            weight = _dragTangentWeightPreview;
        }

        var direction = GetNormalizedTangentDirection(tangent);
        var sign = isArrive ? -1.0 : 1.0;

        var time = key.Time + sign * direction.X * weight;
        var value = key.Value + sign * direction.Y * weight;

        return DataToScreen(time, value, plot);
    }

    /**
     * Unreal Curve의 실제 Control Point와 같은 Handle 길이를 구한다.
     * Weighted는 저장된 Weight, Unweighted는 해당 Segment의 dt/3이다.
     */
    private double GetDisplayTangentWeight(
        int index,
        bool isArrive,
        double tangent
    )
    {
        var key = _keys[index];
        var isWeighted = isArrive
            ? IsArriveWeighted(key)
            : IsLeaveWeighted(key);

        if (isWeighted)
        {
            return Math.Max(
                0.0,
                isArrive
                    ? key.ArriveTangentWeight
                    : key.LeaveTangentWeight
            );
        }

        double segmentTime;
        if (isArrive && index > 0)
        {
            segmentTime = key.Time - _keys[index - 1].Time;
        }
        else if (!isArrive && index < _keys.Count - 1)
        {
            segmentTime = _keys[index + 1].Time - key.Time;
        }
        else
        {
            /* 시작/끝의 사용되지 않는 반대쪽 Handle은 화면 기준으로만 표시한다. */
            segmentTime = Math.Max((_maxTime - _minTime) * 0.18, 0.001);
        }

        return GetDefaultTangentWeight(
            Math.Max(segmentTime, 0.000001),
            tangent
        );
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

        if (_selectedIndex >= 0 &&
            _selectedIndex < _keys.Count &&
            IsCubic(_keys[_selectedIndex].InterpMode))
        {
            if (HitTangentHandle(mouse, plot, out var isArrive))
            {
                _draggingTangent = true;
                _draggingArriveTangent = isArrive;
                _dragTangentIndex = _selectedIndex;
                _dragTangentPreview = isArrive
                    ? _keys[_selectedIndex].ArriveTangent
                    : _keys[_selectedIndex].LeaveTangent;
                _dragTangentWeightPreview = (float)GetDisplayTangentWeight(
                    _selectedIndex,
                    isArrive,
                    _dragTangentPreview
                );
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        var keyIndex = HitKey(mouse, plot);

        if (keyIndex >= 0)
        {
            _selectedIndex = keyIndex;
            KeySelected?.Invoke(
                this,
                new FloatCurveKeySelectedEventArgs(keyIndex)
            );

            _draggingKey = true;
            _dragKeyIndex = keyIndex;
            _dragPreview = CloneKey(_keys[keyIndex]);
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
                new FloatCurveAddKeyRequestedEventArgs(
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
        if (_draggingKey)
        {
            ReleaseMouseCapture();

            if (_dragPreview is not null &&
                _dragKeyIndex >= 0)
            {
                KeyMoveCommitted?.Invoke(
                    this,
                    new FloatCurveKeyMoveCommittedEventArgs(
                        _dragKeyIndex,
                        _dragPreview.Time,
                        _dragPreview.Value
                    )
                );
            }

            _draggingKey = false;
            _dragKeyIndex = -1;
            _dragPreview = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_draggingTangent)
        {
            ReleaseMouseCapture();

            if (_dragTangentIndex >= 0)
            {
                TangentMoveCommitted?.Invoke(
                    this,
                    new FloatCurveTangentMoveCommittedEventArgs(
                        _dragTangentIndex,
                        _draggingArriveTangent,
                        _dragTangentPreview,
                        _dragTangentWeightPreview
                    )
                );
            }

            _draggingTangent = false;
            _dragTangentIndex = -1;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void HandleMouseMove(
        object sender,
        MouseEventArgs e
    )
    {
        var mouse = e.GetPosition(this);
        var plot = GetPlotRect();

        if (_draggingKey &&
            _dragPreview is not null &&
            _dragKeyIndex >= 0)
        {
            var data = ScreenToData(mouse, plot);
            var time = (float)data.Time;
            var value = (float)data.Value;

            var epsilon =
                (float)Math.Max(
                    (_maxTime - _minTime) * 0.0001,
                    0.000001
                );

            if (_dragKeyIndex > 0)
            {
                time = Math.Max(
                    time,
                    _keys[_dragKeyIndex - 1].Time + epsilon
                );
            }

            if (_dragKeyIndex < _keys.Count - 1)
            {
                time = Math.Min(
                    time,
                    _keys[_dragKeyIndex + 1].Time - epsilon
                );
            }

            _dragPreview.Time = time;
            _dragPreview.Value = value;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_draggingTangent &&
            _dragTangentIndex >= 0 &&
            _dragTangentIndex < _keys.Count)
        {
            var data = ScreenToData(mouse, plot);
            var key = _keys[_dragTangentIndex];

            var dt = (float)data.Time - key.Time;
            if (_draggingArriveTangent)
                dt = Math.Min(dt, -0.000001f);
            else
                dt = Math.Max(dt, 0.000001f);

            var dv = (float)data.Value - key.Value;

            _dragTangentPreview = dv / dt;

            /*
             * Weighted Tangent Handle을 끌 때는 기울기뿐 아니라
             * Handle 길이(Weight)도 Unreal처럼 같이 바뀐다.
             */
            _dragTangentWeightPreview =
                MathF.Sqrt(dt * dt + dv * dv);

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
        var factor = e.Delta > 0
            ? 0.82
            : 1.0 / 0.82;

        _minTime =
            anchor.Time +
            (_minTime - anchor.Time) * factor;
        _maxTime =
            anchor.Time +
            (_maxTime - anchor.Time) * factor;
        _minValue =
            anchor.Value +
            (_minValue - anchor.Value) * factor;
        _maxValue =
            anchor.Value +
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
        if (e.ChangedButton != MouseButton.Middle &&
            e.ChangedButton != MouseButton.Right)
        {
            return;
        }

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
        if (!_panning ||
            (e.ChangedButton != MouseButton.Middle &&
             e.ChangedButton != MouseButton.Right))
        {
            return;
        }

        _panning = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private int HitKey(Point mouse, Rect plot)
    {
        var display = GetDisplayKeys();

        for (var i = 0; i < display.Count; i++)
        {
            var p = DataToScreen(
                display[i].Time,
                display[i].Value,
                plot
            );

            var dx = mouse.X - p.X;
            var dy = mouse.Y - p.Y;

            if (dx * dx + dy * dy <= HitRadius * HitRadius)
                return i;
        }

        return -1;
    }

    private bool HitTangentHandle(
        Point mouse,
        Rect plot,
        out bool isArrive
    )
    {
        isArrive = false;

        var arrive = GetTangentHandlePoint(
            _selectedIndex,
            true,
            plot
        );
        var leave = GetTangentHandlePoint(
            _selectedIndex,
            false,
            plot
        );

        if (DistanceSquared(mouse, arrive) <=
            HitRadius * HitRadius)
        {
            isArrive = true;
            return true;
        }

        if (DistanceSquared(mouse, leave) <=
            HitRadius * HitRadius)
        {
            isArrive = false;
            return true;
        }

        return false;
    }

    private List<FloatCurveKeyDocument> GetDisplayKeys()
    {
        var result =
            _keys.Select(CloneKey).ToList();

        if (_draggingKey &&
            _dragPreview is not null &&
            _dragKeyIndex >= 0 &&
            _dragKeyIndex < result.Count)
        {
            result[_dragKeyIndex] =
                CloneKey(_dragPreview);
        }

        if (_draggingTangent &&
            _dragTangentIndex >= 0 &&
            _dragTangentIndex < result.Count)
        {
            var key = result[_dragTangentIndex];

            if (_draggingArriveTangent)
            {
                key.ArriveTangent = _dragTangentPreview;
                if (IsArriveWeighted(key))
                    key.ArriveTangentWeight = _dragTangentWeightPreview;
            }
            else
            {
                key.LeaveTangent = _dragTangentPreview;
                if (IsLeaveWeighted(key))
                    key.LeaveTangentWeight = _dragTangentWeightPreview;
            }

            /* User Tangent는 Unreal처럼 양쪽 기울기가 연결되어 있다. */
            if (key.TangentMode.Contains(
                "User",
                StringComparison.OrdinalIgnoreCase
            ))
            {
                key.ArriveTangent = _dragTangentPreview;
                key.LeaveTangent = _dragTangentPreview;
            }
        }

        return result;
    }

    private Rect GetPlotRect()
    {
        var width =
            Math.Max(
                1,
                ActualWidth - LeftMargin - RightMargin
            );

        var height =
            Math.Max(
                1,
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
        var x =
            plot.Left +
            (time - _minTime) /
            (_maxTime - _minTime) *
            plot.Width;

        var y =
            plot.Top +
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
        var xRatio =
            (point.X - plot.Left) / plot.Width;
        var yRatio =
            (point.Y - plot.Top) / plot.Height;

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
            _minTime = 0;
            _maxTime = 1;
        }

        if (!double.IsFinite(_minValue) ||
            !double.IsFinite(_maxValue) ||
            Math.Abs(_maxValue - _minValue) < 1e-9)
        {
            _minValue = 0;
            _maxValue = 1;
        }
    }

    private void CancelDrag()
    {
        _draggingKey = false;
        _dragKeyIndex = -1;
        _dragPreview = null;
        _draggingTangent = false;
        _dragTangentIndex = -1;
        _dragTangentWeightPreview = 0;
    }

    private static bool IsConstant(string value) =>
        value.Contains(
            "Constant",
            StringComparison.OrdinalIgnoreCase
        );

    private static bool IsCubic(string value) =>
        value.Contains(
            "Cubic",
            StringComparison.OrdinalIgnoreCase
        );

    private static bool IsArriveWeighted(FloatCurveKeyDocument key) =>
        key.TangentWeightMode.Contains(
            "WeightedArrive",
            StringComparison.OrdinalIgnoreCase
        ) ||
        key.TangentWeightMode.Contains(
            "WeightedBoth",
            StringComparison.OrdinalIgnoreCase
        );

    private static bool IsLeaveWeighted(FloatCurveKeyDocument key) =>
        key.TangentWeightMode.Contains(
            "WeightedLeave",
            StringComparison.OrdinalIgnoreCase
        ) ||
        key.TangentWeightMode.Contains(
            "WeightedBoth",
            StringComparison.OrdinalIgnoreCase
        );

    private static double DistanceSquared(
        Point a,
        Point b
    )
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static FloatCurveKeyDocument CloneKey(
        FloatCurveKeyDocument key
    ) =>
        new()
        {
            InterpMode = key.InterpMode,
            TangentMode = key.TangentMode,
            TangentWeightMode = key.TangentWeightMode,
            Time = key.Time,
            Value = key.Value,
            ArriveTangent = key.ArriveTangent,
            ArriveTangentWeight = key.ArriveTangentWeight,
            LeaveTangent = key.LeaveTangent,
            LeaveTangentWeight = key.LeaveTangentWeight
        };

    private static string FormatAxis(double value)
    {
        if (Math.Abs(value) >= 10000 ||
            (Math.Abs(value) > 0 &&
             Math.Abs(value) < 0.001))
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
            VisualTreeHelper
                .GetDpi(this)
                .PixelsPerDip
        );

        dc.DrawText(formatted, point);
    }
}
