using JsonAssetDataEditor.Core;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JsonAssetDataEditor.Editors;

/**
 * Unreal UCurveFloat / FRichCurve 전용 편집기.
 *
 * CurveTable은 기존 표 편집기를 유지하고,
 * Curve Float만 Unreal Curve Editor에 가까운 그래프 기반 UI를 사용한다.
 */
public sealed class FloatCurveEditorControl : UserControl, IDataEditor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    private readonly FloatCurveGraphControl _graph = new();
    private readonly SnapshotHistory _history = new();

    private readonly TextBox _timeBox = new();
    private readonly TextBox _valueBox = new();
    private readonly TextBox _arriveTangentBox = new();
    private readonly TextBox _leaveTangentBox = new();
    private readonly ComboBox _preExtrapCombo = new();
    private readonly ComboBox _postExtrapCombo = new();
    private readonly TextBlock _selectionText = new();

    private FloatCurveDocument _document = new();
    private int _selectedIndex = -1;
    private bool _isDirty;
    private string _savedSnapshot = string.Empty;
    private bool _suppressUiEvents;

    public ManifestEntry Entry { get; }

    public bool IsDirty => _isDirty;
    public event EventHandler? DirtyStateChanged;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool SupportsCsv => false;

    public FloatCurveEditorControl(
        ManifestEntry entry,
        string jsonText
    )
    {
        Entry = entry;
        BuildUi();
        LoadSerializedDocument(jsonText, resetHistory: true);
    }

    private void BuildUi()
    {
        var root = new DockPanel();

        var toolbar = new WrapPanel
        {
            Margin = new Thickness(8, 6, 8, 6)
        };

        toolbar.Children.Add(MakeButton("+ Key", AddKey));
        toolbar.Children.Add(MakeButton("Key 삭제", DeleteSelectedKey, 4));

        toolbar.Children.Add(MakeSeparator());

        toolbar.Children.Add(MakeButton("Constant", () => SetInterpMode("RCIM_Constant")));
        toolbar.Children.Add(MakeButton("Linear", () => SetInterpMode("RCIM_Linear"), 4));
        toolbar.Children.Add(MakeButton("Cubic", () => SetInterpMode("RCIM_Cubic"), 4));

        toolbar.Children.Add(MakeSeparator());

        toolbar.Children.Add(MakeButton("Auto", () => SetTangentMode("RCTM_Auto")));
        toolbar.Children.Add(MakeButton("User", () => SetTangentMode("RCTM_User"), 4));
        toolbar.Children.Add(MakeButton("Break", () => SetTangentMode("RCTM_Break"), 4));

        toolbar.Children.Add(MakeSeparator());

        toolbar.Children.Add(MakeButton("화면 맞춤", _graph.FitView));

        var help = new TextBlock
        {
            Margin = new Thickness(12, 5, 0, 0),
            Text = "점 드래그: Time/Value · Tangent Handle 드래그: 기울기 · 더블클릭: Key 추가 · 휠: Zoom · 가운데/RMB Drag: Pan · F: Fit",
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbar.Children.Add(help);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var settingsBar = new WrapPanel
        {
            Margin = new Thickness(8, 0, 8, 6)
        };

        settingsBar.Children.Add(new TextBlock
        {
            Text = "Pre Infinity",
            Margin = new Thickness(0, 5, 4, 0)
        });
        ConfigureExtrapCombo(_preExtrapCombo);
        _preExtrapCombo.SelectionChanged += (_, _) => ApplyExtrapolationFromUi();
        settingsBar.Children.Add(_preExtrapCombo);

        settingsBar.Children.Add(new TextBlock
        {
            Text = "Post Infinity",
            Margin = new Thickness(12, 5, 4, 0)
        });
        ConfigureExtrapCombo(_postExtrapCombo);
        _postExtrapCombo.SelectionChanged += (_, _) => ApplyExtrapolationFromUi();
        settingsBar.Children.Add(_postExtrapCombo);

        settingsBar.Children.Add(new TextBlock
        {
            Text = "※ Weighted Tangent는 그래프 표시와 Handle Drag에 실제 Weight를 반영합니다.",
            Margin = new Thickness(14, 5, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        DockPanel.SetDock(settingsBar, Dock.Top);
        root.Children.Add(settingsBar);

        var selectionBar = new WrapPanel
        {
            Margin = new Thickness(8, 0, 8, 6)
        };

        _selectionText.Text = "선택 Key: 없음";
        _selectionText.MinWidth = 110;
        _selectionText.Margin = new Thickness(0, 5, 6, 0);
        selectionBar.Children.Add(_selectionText);

        selectionBar.Children.Add(MakeLabel("Time"));
        ConfigureNumberBox(_timeBox);
        selectionBar.Children.Add(_timeBox);

        selectionBar.Children.Add(MakeLabel("Value", 10));
        ConfigureNumberBox(_valueBox);
        selectionBar.Children.Add(_valueBox);

        selectionBar.Children.Add(MakeLabel("Arrive Tangent", 10));
        ConfigureNumberBox(_arriveTangentBox, 110);
        selectionBar.Children.Add(_arriveTangentBox);

        selectionBar.Children.Add(MakeLabel("Leave Tangent", 10));
        ConfigureNumberBox(_leaveTangentBox, 110);
        selectionBar.Children.Add(_leaveTangentBox);

        selectionBar.Children.Add(MakeButton("정밀 값 적용", ApplySelectedNumericValues, 8));

        DockPanel.SetDock(selectionBar, Dock.Top);
        root.Children.Add(selectionBar);

        _graph.Margin = new Thickness(8, 0, 8, 8);
        _graph.MinHeight = 360;
        _graph.KeySelected += Graph_KeySelected;
        _graph.KeyMoveCommitted += Graph_KeyMoveCommitted;
        _graph.TangentMoveCommitted += Graph_TangentMoveCommitted;
        _graph.AddKeyRequested += Graph_AddKeyRequested;
        _graph.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F)
            {
                _graph.FitView();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelectedKey();
                e.Handled = true;
            }
        };

        root.Children.Add(_graph);
        Content = root;
    }

    private static Button MakeButton(
        string text,
        Action action,
        double left = 0
    )
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 66,
            Margin = new Thickness(left, 0, 0, 0),
            Padding = new Thickness(8, 3, 8, 3)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static FrameworkElement MakeSeparator() =>
        new Border
        {
            Width = 1,
            Height = 24,
            Margin = new Thickness(10, 0, 10, 0),
            Background = SystemColors.ControlDarkBrush
        };

    private static TextBlock MakeLabel(
        string text,
        double left = 0
    ) =>
        new()
        {
            Text = text,
            Margin = new Thickness(left, 5, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

    private static void ConfigureNumberBox(
        TextBox box,
        double width = 90
    )
    {
        box.Width = width;
        box.IsEnabled = false;
        box.Margin = new Thickness(0, 0, 0, 0);
    }

    private static void ConfigureExtrapCombo(ComboBox combo)
    {
        combo.Width = 140;
        combo.ItemsSource = new[]
        {
            "RCCE_Constant",
            "RCCE_Linear",
            "RCCE_Cycle",
            "RCCE_CycleWithOffset",
            "RCCE_Oscillate"
        };
    }

    public IReadOnlyList<ValidationIssue> ValidateDocument()
    {
        var issues = new List<ValidationIssue>();

        _document.Keys ??= [];

        for (var i = 0; i < _document.Keys.Count; i++)
        {
            var key = _document.Keys[i];

            if (!float.IsFinite(key.Time))
                issues.Add(new ValidationIssue($"keys[{i}].time", "유효한 float 값이 아닙니다."));

            if (!float.IsFinite(key.Value))
                issues.Add(new ValidationIssue($"keys[{i}].value", "유효한 float 값이 아닙니다."));

            if (!float.IsFinite(key.ArriveTangent))
                issues.Add(new ValidationIssue($"keys[{i}].arriveTangent", "유효한 float 값이 아닙니다."));

            if (!float.IsFinite(key.LeaveTangent))
                issues.Add(new ValidationIssue($"keys[{i}].leaveTangent", "유효한 float 값이 아닙니다."));
        }

        var ordered = _document.Keys
            .Select((key, index) => (key, index))
            .OrderBy(x => x.key.Time)
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            if (NearlyEqual(
                ordered[i - 1].key.Time,
                ordered[i].key.Time
            ))
            {
                issues.Add(
                    new ValidationIssue(
                        $"keys[{ordered[i].index}].time",
                        $"동일한 Time의 Key가 중복되었습니다: {FormatFloat(ordered[i].key.Time)}"
                    )
                );
            }
        }

        return issues;
    }

    public string SerializeDocument(bool indented = true)
    {
        NormalizeDocument();
        var options = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(_document, options);
    }

    public void LoadSerializedDocument(
        string json,
        bool resetHistory
    )
    {
        var parsed =
            JsonSerializer.Deserialize<FloatCurveDocument>(
                json,
                JsonOptions
            ) ?? throw new InvalidDataException(
                "Curve Float JSON을 읽지 못했습니다."
            );

        parsed.Keys ??= [];
        _document = parsed;
        NormalizeDocument();

        if (resetHistory)
        {
            _history.Clear();
            _savedSnapshot = SerializeDocument(indented: false);
            SetDirty(false);
        }

        _selectedIndex = -1;
        RefreshUi(fitGraph: true);
    }

    public void MarkSaved()
    {
        _savedSnapshot = SerializeDocument(indented: false);
        SetDirty(false);
    }

    public void Undo()
    {
        var current = SerializeDocument(indented: false);
        var snapshot = _history.Undo(current);
        if (snapshot is null) return;

        LoadSerializedDocument(snapshot, resetHistory: false);
        UpdateDirtyFromSnapshot();
    }

    public void Redo()
    {
        var current = SerializeDocument(indented: false);
        var snapshot = _history.Redo(current);
        if (snapshot is null) return;

        LoadSerializedDocument(snapshot, resetHistory: false);
        UpdateDirtyFromSnapshot();
    }

    public void ImportCsv(string csvText) =>
        throw new NotSupportedException("Curve Float는 CSV 가져오기를 지원하지 않습니다.");

    public string ExportCsv() =>
        throw new NotSupportedException("Curve Float는 CSV 내보내기를 지원하지 않습니다.");

    public void ExpandAll() { }
    public void CollapseAll() { }

    private void AddKey()
    {
        var suggested = _document.Keys.Count == 0
            ? new Point(0, 0)
            : new Point(
                _document.Keys[^1].Time + 1.0,
                _document.Keys[^1].Value
            );

        AddKeyAt((float)suggested.X, (float)suggested.Y);
    }

    private void Graph_AddKeyRequested(
        object? sender,
        FloatCurveAddKeyRequestedEventArgs e
    ) => AddKeyAt(e.Time, e.Value);

    private void AddKeyAt(float time, float value)
    {
        if (_document.Keys.Any(x => NearlyEqual(x.Time, time)))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Time {FormatFloat(time)}에 이미 Key가 있습니다.",
                "Key 추가 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        PushHistory();

        var key = new FloatCurveKeyDocument
        {
            Time = time,
            Value = value,
            InterpMode = "RCIM_Cubic",
            TangentMode = "RCTM_Auto",
            TangentWeightMode = "RCTWM_WeightedNone",
            ArriveTangent = 0,
            LeaveTangent = 0,
            ArriveTangentWeight = 0,
            LeaveTangentWeight = 0
        };

        _document.Keys.Add(key);
        NormalizeDocument();
        RecalculateAutoTangents();

        _selectedIndex = _document.Keys.IndexOf(key);
        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void DeleteSelectedKey()
    {
        if (_selectedIndex < 0 ||
            _selectedIndex >= _document.Keys.Count)
        {
            return;
        }

        PushHistory();
        _document.Keys.RemoveAt(_selectedIndex);
        _selectedIndex = Math.Min(
            _selectedIndex,
            _document.Keys.Count - 1
        );

        RecalculateAutoTangents();
        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void SetInterpMode(string mode)
    {
        var key = SelectedKey;
        if (key is null) return;

        PushHistory();
        key.InterpMode = mode;

        if (mode.Equals(
            "RCIM_Cubic",
            StringComparison.OrdinalIgnoreCase
        ) &&
            key.TangentMode.Equals(
                "RCTM_Auto",
                StringComparison.OrdinalIgnoreCase
            ))
        {
            RecalculateAutoTangents();
        }

        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void SetTangentMode(string mode)
    {
        var key = SelectedKey;
        if (key is null) return;

        PushHistory();
        key.TangentMode = mode;

        if (mode.Equals(
            "RCTM_Auto",
            StringComparison.OrdinalIgnoreCase
        ))
        {
            RecalculateAutoTangents();
        }
        else if (mode.Equals(
            "RCTM_User",
            StringComparison.OrdinalIgnoreCase
        ))
        {
            // User Tangent는 양쪽 기울기를 연결된 상태로 시작한다.
            var tangent =
                (key.ArriveTangent + key.LeaveTangent) * 0.5f;
            key.ArriveTangent = tangent;
            key.LeaveTangent = tangent;
        }

        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void ApplySelectedNumericValues()
    {
        var key = SelectedKey;
        if (key is null) return;

        if (!TryParseFloat(_timeBox.Text, out var time) ||
            !TryParseFloat(_valueBox.Text, out var value) ||
            !TryParseFloat(_arriveTangentBox.Text, out var arrive) ||
            !TryParseFloat(_leaveTangentBox.Text, out var leave))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Time, Value, Tangent는 모두 float 숫자여야 합니다.",
                "정밀 값 적용 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        var otherIndex = _document.Keys.FindIndex(
            x => !ReferenceEquals(x, key) &&
                 NearlyEqual(x.Time, time)
        );

        if (otherIndex >= 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Time {FormatFloat(time)}에 이미 다른 Key가 있습니다.",
                "정밀 값 적용 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        PushHistory();

        key.Time = time;
        key.Value = value;
        key.ArriveTangent = arrive;
        key.LeaveTangent = leave;

        if (key.TangentMode.Equals(
            "RCTM_User",
            StringComparison.OrdinalIgnoreCase
        ))
        {
            key.LeaveTangent = key.ArriveTangent;
        }

        NormalizeDocument();
        _selectedIndex = _document.Keys.IndexOf(key);
        RecalculateAutoTangents();

        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void Graph_KeySelected(
        object? sender,
        FloatCurveKeySelectedEventArgs e
    )
    {
        _selectedIndex = e.Index;
        RefreshSelectionFields();
    }

    private void Graph_KeyMoveCommitted(
        object? sender,
        FloatCurveKeyMoveCommittedEventArgs e
    )
    {
        if (e.Index < 0 || e.Index >= _document.Keys.Count)
            return;

        var key = _document.Keys[e.Index];

        var hasCollision = _document.Keys
            .Where((_, index) => index != e.Index)
            .Any(x => NearlyEqual(x.Time, e.NewTime));

        if (hasCollision)
        {
            RefreshUi(fitGraph: false);
            return;
        }

        PushHistory();
        key.Time = e.NewTime;
        key.Value = e.NewValue;

        NormalizeDocument();
        _selectedIndex = _document.Keys.IndexOf(key);
        RecalculateAutoTangents();

        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void Graph_TangentMoveCommitted(
        object? sender,
        FloatCurveTangentMoveCommittedEventArgs e
    )
    {
        if (e.Index < 0 || e.Index >= _document.Keys.Count)
            return;

        var key = _document.Keys[e.Index];

        PushHistory();

        /*
         * Auto Tangent를 사용자가 직접 움직이면
         * Unreal Curve Editor와 유사하게 User Tangent로 전환한다.
         */
        if (key.TangentMode.Equals(
            "RCTM_Auto",
            StringComparison.OrdinalIgnoreCase
        ))
        {
            key.TangentMode = "RCTM_User";
        }

        if (e.IsArrive)
        {
            key.ArriveTangent = e.Tangent;

            if (IsArriveWeighted(key.TangentWeightMode))
                key.ArriveTangentWeight = e.TangentWeight;
        }
        else
        {
            key.LeaveTangent = e.Tangent;

            if (IsLeaveWeighted(key.TangentWeightMode))
                key.LeaveTangentWeight = e.TangentWeight;
        }

        if (key.TangentMode.Equals(
            "RCTM_User",
            StringComparison.OrdinalIgnoreCase
        ))
        {
            // User는 좌/우 Tangent 기울기를 연결한다.
            key.ArriveTangent = e.Tangent;
            key.LeaveTangent = e.Tangent;
        }

        MarkChanged();
        RefreshUi(fitGraph: false);
    }

    private void ApplyExtrapolationFromUi()
    {
        if (_suppressUiEvents)
            return;

        var pre = _preExtrapCombo.SelectedItem as string;
        var post = _postExtrapCombo.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(pre) ||
            string.IsNullOrWhiteSpace(post))
        {
            return;
        }

        if (_document.PreInfinityExtrap == pre &&
            _document.PostInfinityExtrap == post)
        {
            return;
        }

        PushHistory();
        _document.PreInfinityExtrap = pre;
        _document.PostInfinityExtrap = post;
        MarkChanged();
    }

    private void RefreshUi(bool fitGraph)
    {
        _suppressUiEvents = true;
        try
        {
            _preExtrapCombo.SelectedItem =
                NormalizeExtrap(_document.PreInfinityExtrap);
            _postExtrapCombo.SelectedItem =
                NormalizeExtrap(_document.PostInfinityExtrap);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        _graph.SetCurve(_document.Keys, _selectedIndex, fitGraph);
        RefreshSelectionFields();
    }

    private void RefreshSelectionFields()
    {
        var key = SelectedKey;

        if (key is null)
        {
            _selectionText.Text = "선택 Key: 없음";
            SetNumberBoxesEnabled(false);
            _timeBox.Text = string.Empty;
            _valueBox.Text = string.Empty;
            _arriveTangentBox.Text = string.Empty;
            _leaveTangentBox.Text = string.Empty;
            return;
        }

        _selectionText.Text =
            $"Key {_selectedIndex} · {FriendlyInterp(key.InterpMode)} / {FriendlyTangent(key.TangentMode)}";

        SetNumberBoxesEnabled(true);
        _timeBox.Text = FormatFloat(key.Time);
        _valueBox.Text = FormatFloat(key.Value);
        _arriveTangentBox.Text = FormatFloat(key.ArriveTangent);
        _leaveTangentBox.Text = FormatFloat(key.LeaveTangent);
    }

    private void SetNumberBoxesEnabled(bool enabled)
    {
        _timeBox.IsEnabled = enabled;
        _valueBox.IsEnabled = enabled;
        _arriveTangentBox.IsEnabled = enabled;
        _leaveTangentBox.IsEnabled = enabled;
    }

    private FloatCurveKeyDocument? SelectedKey =>
        _selectedIndex >= 0 &&
        _selectedIndex < _document.Keys.Count
            ? _document.Keys[_selectedIndex]
            : null;

    private void RecalculateAutoTangents()
    {
        /*
         * FRichCurve::AutoSetTangents의 핵심 규칙을 외부 Editor에도 맞춘다.
         *
         * Unreal의 Auto Curve는 시작/끝 Key에서 Plateau가 되므로
         * 양 끝 Auto Tangent는 0이다.
         * 내부 Key는 ComputeCurveTangent(Tension=0, unclamped)의 float 규칙과
         * 동일하게 (NextValue-PrevValue)/(NextTime-PrevTime)를 사용한다.
         *
         * JSON을 처음 읽을 때는 이 함수를 호출하지 않기 때문에 Unreal에서
         * Export된 실제 Tangent는 그대로 보존된다. Key 추가/이동 등으로
         * Auto Tangent를 다시 계산해야 할 때만 이 규칙을 사용한다.
         */
        for (var i = 0; i < _document.Keys.Count; i++)
        {
            var key = _document.Keys[i];
            if (!key.InterpMode.Equals(
                "RCIM_Cubic",
                StringComparison.OrdinalIgnoreCase
            ) ||
                !key.TangentMode.Equals(
                    "RCTM_Auto",
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                continue;
            }

            var slope = CalculateUnrealAutoSlope(i);
            key.ArriveTangent = slope;
            key.LeaveTangent = slope;
        }
    }

    private float CalculateUnrealAutoSlope(int index)
    {
        if (_document.Keys.Count < 2 ||
            index <= 0 ||
            index >= _document.Keys.Count - 1)
        {
            // Unreal Auto Curve의 시작/끝은 Stationary Endpoint다.
            return 0.0f;
        }

        var prev = _document.Keys[index - 1];
        var next = _document.Keys[index + 1];
        var totalDt = next.Time - prev.Time;

        if (Math.Abs(totalDt) < 1e-6f)
            return 0.0f;

        return (next.Value - prev.Value) / totalDt;
    }

    private static bool IsArriveWeighted(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) &&
        (mode.Contains("WeightedArrive", StringComparison.OrdinalIgnoreCase) ||
         mode.Contains("WeightedBoth", StringComparison.OrdinalIgnoreCase));

    private static bool IsLeaveWeighted(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) &&
        (mode.Contains("WeightedLeave", StringComparison.OrdinalIgnoreCase) ||
         mode.Contains("WeightedBoth", StringComparison.OrdinalIgnoreCase));

    private void NormalizeDocument()
    {
        _document.Keys ??= [];
        _document.PreInfinityExtrap =
            NormalizeExtrap(_document.PreInfinityExtrap);
        _document.PostInfinityExtrap =
            NormalizeExtrap(_document.PostInfinityExtrap);

        foreach (var key in _document.Keys)
        {
            key.InterpMode = NormalizeInterp(key.InterpMode);
            key.TangentMode = NormalizeTangent(key.TangentMode);
            key.TangentWeightMode =
                NormalizeWeight(key.TangentWeightMode);
        }

        _document.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private void PushHistory() =>
        _history.PushBeforeChange(
            SerializeDocument(indented: false)
        );

    private void MarkChanged()
    {
        UpdateDirtyFromSnapshot();
    }

    private void UpdateDirtyFromSnapshot()
    {
        SetDirty(
            SerializeDocument(indented: false) !=
            _savedSnapshot
        );
    }

    private void SetDirty(bool value)
    {
        if (_isDirty == value)
            return;

        _isDirty = value;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParseFloat(
        string text,
        out float value
    ) =>
        float.TryParse(
            text.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        ) &&
        float.IsFinite(value);

    internal static string FormatFloat(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static bool NearlyEqual(float a, float b)
    {
        var scale = Math.Max(
            1.0f,
            Math.Max(Math.Abs(a), Math.Abs(b))
        );
        return Math.Abs(a - b) <= 1e-5f * scale;
    }

    private static string NormalizeInterp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "RCIM_Linear";

        if (value.Contains("Constant", StringComparison.OrdinalIgnoreCase))
            return "RCIM_Constant";
        if (value.Contains("Cubic", StringComparison.OrdinalIgnoreCase))
            return "RCIM_Cubic";
        return "RCIM_Linear";
    }

    private static string NormalizeTangent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "RCTM_Auto";

        if (value.Contains("Break", StringComparison.OrdinalIgnoreCase))
            return "RCTM_Break";
        if (value.Contains("User", StringComparison.OrdinalIgnoreCase))
            return "RCTM_User";
        if (value.Contains("None", StringComparison.OrdinalIgnoreCase))
            return "RCTM_None";
        return "RCTM_Auto";
    }

    private static string NormalizeWeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "RCTWM_WeightedNone";

        if (value.Contains("Both", StringComparison.OrdinalIgnoreCase))
            return "RCTWM_WeightedBoth";
        if (value.Contains("Arrive", StringComparison.OrdinalIgnoreCase))
            return "RCTWM_WeightedArrive";
        if (value.Contains("Leave", StringComparison.OrdinalIgnoreCase))
            return "RCTWM_WeightedLeave";
        return "RCTWM_WeightedNone";
    }

    private static string NormalizeExtrap(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "RCCE_Constant";

        if (value.Contains("CycleWithOffset", StringComparison.OrdinalIgnoreCase))
            return "RCCE_CycleWithOffset";
        if (value.Contains("Oscillate", StringComparison.OrdinalIgnoreCase))
            return "RCCE_Oscillate";
        if (value.Contains("Cycle", StringComparison.OrdinalIgnoreCase))
            return "RCCE_Cycle";
        if (value.Contains("Linear", StringComparison.OrdinalIgnoreCase))
            return "RCCE_Linear";
        return "RCCE_Constant";
    }

    private static string FriendlyInterp(string value) =>
        value.Replace("RCIM_", string.Empty);

    private static string FriendlyTangent(string value) =>
        value.Replace("RCTM_", string.Empty);
}

/** FJsonObjectConverter가 FRichCurve에서 생성하는 JSON 구조와 대응한다. */
public sealed class FloatCurveDocument
{
    [JsonPropertyName("defaultValue")]
    public float DefaultValue { get; set; }

    [JsonPropertyName("preInfinityExtrap")]
    public string PreInfinityExtrap { get; set; } = "RCCE_Constant";

    [JsonPropertyName("postInfinityExtrap")]
    public string PostInfinityExtrap { get; set; } = "RCCE_Constant";

    [JsonPropertyName("keys")]
    public List<FloatCurveKeyDocument> Keys { get; set; } = [];
}

/** Unreal FRichCurveKey와 대응하는 외부 편집기 모델이다. */
public sealed class FloatCurveKeyDocument
{
    [JsonPropertyName("interpMode")]
    public string InterpMode { get; set; } = "RCIM_Cubic";

    [JsonPropertyName("tangentMode")]
    public string TangentMode { get; set; } = "RCTM_Auto";

    [JsonPropertyName("tangentWeightMode")]
    public string TangentWeightMode { get; set; } = "RCTWM_WeightedNone";

    [JsonPropertyName("time")]
    public float Time { get; set; }

    [JsonPropertyName("value")]
    public float Value { get; set; }

    [JsonPropertyName("arriveTangent")]
    public float ArriveTangent { get; set; }

    [JsonPropertyName("arriveTangentWeight")]
    public float ArriveTangentWeight { get; set; }

    [JsonPropertyName("leaveTangent")]
    public float LeaveTangent { get; set; }

    [JsonPropertyName("leaveTangentWeight")]
    public float LeaveTangentWeight { get; set; }
}
