using JsonAssetDataEditor.Core;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JsonAssetDataEditor.Editors;

/**
 * Unreal CurveTable JSON을 Spreadsheet 형태로 편집한다.
 *
 * Unreal 기본 JSON 규격:
 * [
 *   { "Name": "CurveA", "0": 1.0, "1": 2.0 }
 * ]
 *
 * 열 Header는 Key Time, 셀 값은 Curve Value다.
 */
public sealed class CurveTableEditorControl : UserControl, IDataEditor
{
    private readonly DataGrid _grid = new();
    private readonly CurveGraphControl _graph = new();
    private readonly ComboBox _curveSelector = new();
    private readonly TextBox _selectedTimeBox = new();
    private readonly TextBox _selectedValueBox = new();
    private readonly TextBlock _selectedKeyText = new();
    private readonly SnapshotHistory _history = new();
    private readonly List<string> _keyTimes = [];
    private bool _syncingCurveSelection;
    private float? _selectedGraphTime;
    private DataTable _table = new();
    private bool _internalChange;
    private string? _pendingSnapshot;
    private string _savedSnapshot = string.Empty;
    private bool _isDirty;

    public ManifestEntry Entry { get; }
    public bool IsDirty => _isDirty;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool SupportsCsv => true;
    public event EventHandler? DirtyStateChanged;

    public CurveTableEditorControl(ManifestEntry entry, string jsonText)
    {
        Entry = entry;
        BuildUi();
        LoadSerializedDocument(jsonText, resetHistory: true);
    }

    private void BuildUi()
    {
        var root = new DockPanel();

        /*
         * 상단 툴바:
         * Curve Row 선택/추가/삭제와 그래프 조작을 배치한다.
         */
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 8, 4)
        };

        toolbar.Children.Add(new TextBlock
        {
            Text = "Curve:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });

        _curveSelector.MinWidth = 180;
        _curveSelector.Margin = new Thickness(0, 0, 8, 0);
        _curveSelector.SelectionChanged += CurveSelector_SelectionChanged;
        toolbar.Children.Add(_curveSelector);

        var addRow = new Button { Content = "Curve 추가" };
        addRow.Click += (_, _) => AddRow();

        var deleteRow = new Button
        {
            Content = "Curve 삭제",
            Margin = new Thickness(4, 0, 0, 0)
        };
        deleteRow.Click += (_, _) => DeleteSelectedRows();

        var addKey = new Button
        {
            Content = "+ Key",
            Margin = new Thickness(12, 0, 0, 0)
        };
        addKey.Click += (_, _) => AddKeyToActiveCurve();

        var deleteKey = new Button
        {
            Content = "선택 Key 삭제",
            Margin = new Thickness(4, 0, 0, 0)
        };
        deleteKey.Click += (_, _) => DeleteSelectedGraphKey();

        var fit = new Button
        {
            Content = "화면 맞춤",
            Margin = new Thickness(12, 0, 0, 0)
        };
        fit.Click += (_, _) => _graph.FitView();

        var hint = new TextBlock
        {
            Text = $"  {GetInterpModeDisplayName()} · 점 드래그: Time/Value 수정 · 그래프 더블클릭: Key 추가 · 휠: 줌 · 가운데 드래그: 이동",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        toolbar.Children.Add(addRow);
        toolbar.Children.Add(deleteRow);
        toolbar.Children.Add(addKey);
        toolbar.Children.Add(deleteKey);
        toolbar.Children.Add(fit);
        toolbar.Children.Add(hint);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        /*
         * 선택된 Key를 숫자로 정밀 수정할 수 있는 보조 바다.
         */
        var keyBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 2, 8, 6)
        };

        _selectedKeyText.Text = "선택 Key: 없음";
        _selectedKeyText.VerticalAlignment = VerticalAlignment.Center;
        _selectedKeyText.MinWidth = 110;

        keyBar.Children.Add(_selectedKeyText);
        keyBar.Children.Add(new TextBlock
        {
            Text = "Time",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        });

        _selectedTimeBox.Width = 100;
        _selectedTimeBox.IsEnabled = false;
        keyBar.Children.Add(_selectedTimeBox);

        keyBar.Children.Add(new TextBlock
        {
            Text = "Value",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0)
        });

        _selectedValueBox.Width = 100;
        _selectedValueBox.IsEnabled = false;
        keyBar.Children.Add(_selectedValueBox);

        var applySelectedKey = new Button
        {
            Content = "정밀 값 적용",
            Margin = new Thickness(6, 0, 0, 0)
        };
        applySelectedKey.Click += (_, _) => ApplySelectedKeyNumericValues();
        keyBar.Children.Add(applySelectedKey);

        if ((Entry.InterpMode ?? 0) == 2)
        {
            keyBar.Children.Add(new TextBlock
            {
                Text = "  ※ Cubic 그래프는 현재 JSON에 Tangent 정보가 없어 Auto Tangent 기준 미리보기입니다.",
                Foreground = Brushes.DarkOrange,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
        }

        DockPanel.SetDock(keyBar, Dock.Top);
        root.Children.Add(keyBar);

        /*
         * 그래프를 메인으로, 기존 표를 하단 정밀 편집용으로 유지한다.
         */
        var main = new Grid
        {
            Margin = new Thickness(8, 0, 8, 8)
        };
        main.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(3, GridUnitType.Star),
            MinHeight = 260
        });
        main.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(6)
        });
        main.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(2, GridUnitType.Star),
            MinHeight = 160
        });

        _graph.MinHeight = 240;
        _graph.InterpMode = Entry.InterpMode ?? 0;
        _graph.KeySelected += Graph_KeySelected;
        _graph.KeyMoveCommitted += Graph_KeyMoveCommitted;
        _graph.AddKeyRequested += Graph_AddKeyRequested;
        Grid.SetRow(_graph, 0);
        main.Children.Add(_graph);

        var splitter = new GridSplitter
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(splitter, 1);
        main.Children.Add(splitter);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = true;
        _grid.CanUserDeleteRows = true;
        _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        _grid.HeadersVisibility = DataGridHeadersVisibility.All;
        _grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        _grid.BeginningEdit += Grid_BeginningEdit;
        _grid.CellEditEnding += Grid_CellEditEnding;
        _grid.RowEditEnding += Grid_RowEditEnding;
        _grid.PreviewKeyDown += Grid_PreviewKeyDown;
        _grid.SelectionChanged += Grid_SelectionChanged;
        _grid.CurrentCellChanged += Grid_CurrentCellChanged;
        Grid.SetRow(_grid, 2);
        main.Children.Add(_grid);

        root.Children.Add(main);
        Content = root;
    }

    public void LoadSerializedDocument(string json, bool resetHistory)
    {
        _internalChange = true;
        try
        {
            var root = JsonNode.Parse(json) as JsonArray
                       ?? throw new InvalidDataException("CurveTable JSON 최상위는 Array여야 합니다.");

            _keyTimes.Clear();

            // JSON의 Key Time들을 float 기준으로 정규화해서 모은다.
            var keySet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.OfType<JsonObject>())
            {
                foreach (var pair in item)
                {
                    if (pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!float.TryParse(pair.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                        continue;

                    keySet.Add(FormatFloat(time));
                }
            }

            _keyTimes.AddRange(
                keySet.OrderBy(x => float.Parse(x, CultureInfo.InvariantCulture))
            );

            var table = new DataTable();
            table.Columns.Add("Name", typeof(object));
            foreach (var keyTime in _keyTimes)
                table.Columns.Add(keyTime, typeof(object));

            foreach (var item in root)
            {
                if (item is not JsonObject obj)
                    continue;

                var row = table.NewRow();
                row["Name"] = obj["Name"]?.GetValue<string>() ?? string.Empty;

                foreach (var pair in obj)
                {
                    if (pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!float.TryParse(pair.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                        continue;

                    var canonicalKey = FormatFloat(time);
                    if (!table.Columns.Contains(canonicalKey))
                        continue;

                    if (TryReadFloat(pair.Value, out var value))
                        row[canonicalKey] = FormatFloat(value);
                }

                table.Rows.Add(row);
            }

            _table = table;
            _grid.ItemsSource = _table.DefaultView;
            BuildGridColumns();
            RefreshCurveSelectorAndGraph(fitGraph: true);

            if (resetHistory)
            {
                _history.Clear();
                _savedSnapshot = SerializeDocument();
                SetDirty(false);
            }
            else
            {
                UpdateDirtyFromSnapshot();
            }
        }
        finally
        {
            _internalChange = false;
        }
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Name",
            MinWidth = 160,
            Width = DataGridLength.SizeToHeader,
            Binding = new Binding("[Name]")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            }
        });

        foreach (var keyTime in _keyTimes)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = keyTime,
                MinWidth = 90,
                Width = DataGridLength.SizeToHeader,
                Binding = new Binding($"[{keyTime}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                }
            });
        }
    }

    public string SerializeDocument(bool indented = true)
    {
        var array = new JsonArray();

        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted || IsEmptyNewRow(row))
                continue;

            var name = Convert.ToString(row["Name"])?.Trim() ?? string.Empty;
            var obj = new JsonObject
            {
                ["Name"] = name
            };

            foreach (var keyTime in _keyTimes)
            {
                var text = Convert.ToString(row[keyTime])?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    obj[keyTime] = JsonValue.Create(value);
                else
                    obj[keyTime] = JsonValue.Create(text);
            }

            array.Add(obj);
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    public IReadOnlyList<ValidationIssue> ValidateDocument()
    {
        var issues = new List<ValidationIssue>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var rowIndex = 0; rowIndex < _table.Rows.Count; rowIndex++)
        {
            var row = _table.Rows[rowIndex];
            if (row.RowState == DataRowState.Deleted || IsEmptyNewRow(row))
                continue;

            var name = Convert.ToString(row["Name"])?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                issues.Add(new ValidationIssue($"[{rowIndex}].Name", "Curve Row Name은 비어 있을 수 없습니다."));
            else if (!names.Add(name))
                issues.Add(new ValidationIssue($"[{rowIndex}].Name", "Curve Row Name이 중복되었습니다."));

            var keyCount = 0;
            foreach (var keyTime in _keyTimes)
            {
                var text = Convert.ToString(row[keyTime])?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                keyCount++;
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    issues.Add(new ValidationIssue($"[{rowIndex}].{keyTime}", "Curve Value는 float 숫자여야 합니다."));
            }

            if (keyCount == 0)
                issues.Add(new ValidationIssue($"[{rowIndex}]", "Curve Row에는 최소 1개의 Key Value가 필요합니다."));
        }

        return issues;
    }

    public void MarkSaved()
    {
        _savedSnapshot = SerializeDocument();
        SetDirty(false);
    }

    public void Undo()
    {
        var snapshot = _history.Undo(SerializeDocument());
        if (snapshot is not null)
            LoadSerializedDocument(snapshot, resetHistory: false);
    }

    public void Redo()
    {
        var snapshot = _history.Redo(SerializeDocument());
        if (snapshot is not null)
            LoadSerializedDocument(snapshot, resetHistory: false);
    }

    public void ImportCsv(string csvText)
    {
        var rows = CsvCodec.Parse(csvText);
        if (rows.Count == 0)
            throw new InvalidDataException("CSV가 비어 있습니다.");

        var header = rows[0];
        if (header.Count == 0 ||
            !header[0].Equals("Name", StringComparison.OrdinalIgnoreCase) &&
            !header[0].Equals("---", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("CurveTable CSV 첫 번째 열은 Name 또는 --- 이어야 합니다.");
        }

        var canonicalTimes = new List<string>();
        for (var i = 1; i < header.Count; i++)
        {
            if (!float.TryParse(header[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                throw new InvalidDataException($"CurveTable CSV Header의 Key Time이 숫자가 아닙니다: {header[i]}");

            var canonical = FormatFloat(time);
            if (canonicalTimes.Contains(canonical, StringComparer.Ordinal))
                throw new InvalidDataException($"중복된 Key Time입니다: {canonical}");

            canonicalTimes.Add(canonical);
        }

        var array = new JsonArray();
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].All(string.IsNullOrWhiteSpace))
                continue;

            var obj = new JsonObject
            {
                ["Name"] = rows[r].ElementAtOrDefault(0)?.Trim() ?? string.Empty
            };

            for (var c = 1; c < header.Count; c++)
            {
                var cell = rows[r].ElementAtOrDefault(c)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cell))
                    continue;

                if (!float.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException($"CSV {r + 1}행 {c + 1}열의 Curve Value가 숫자가 아닙니다.");

                obj[canonicalTimes[c - 1]] = JsonValue.Create(value);
            }

            array.Add(obj);
        }

        PushHistory();
        LoadSerializedDocument(
            array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            resetHistory: false
        );
        SetDirty(true);
    }

    public string ExportCsv()
    {
        var rows = new List<List<string>>
        {
            new List<string> { "Name" }.Concat(_keyTimes).ToList()
        };

        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted || IsEmptyNewRow(row))
                continue;

            var output = new List<string>
            {
                Convert.ToString(row["Name"]) ?? string.Empty
            };
            output.AddRange(
                _keyTimes.Select(x => Convert.ToString(row[x]) ?? string.Empty)
            );
            rows.Add(output);
        }

        return CsvCodec.Write(rows);
    }

    public void ExpandAll() { }
    public void CollapseAll() { }

    private void AddRow()
    {
        PushHistory();

        var row = _table.NewRow();
        row["Name"] = MakeUniqueRowName();
        foreach (var keyTime in _keyTimes)
            row[keyTime] = string.Empty;

        _table.Rows.Add(row);
        SetDirty(true);

        _grid.SelectedItem = _table.DefaultView[_table.DefaultView.Count - 1];
        _grid.ScrollIntoView(_grid.SelectedItem);
        RefreshCurveSelectorAndGraph(fitGraph: true);
    }

    private void DeleteSelectedRows()
    {
        var rows = _grid.SelectedCells
            .Select(x => x.Item)
            .OfType<DataRowView>()
            .Distinct()
            .ToList();

        if (rows.Count == 0 && _grid.CurrentItem is DataRowView current)
            rows.Add(current);

        if (rows.Count == 0)
            return;

        PushHistory();
        foreach (var row in rows)
            row.Delete();

        SetDirty(true);
        RefreshCurveSelectorAndGraph(fitGraph: true);
    }

    private void AddKeyTime()
    {
        var dialog = new CurveKeyTimeDialog
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
            return;

        if (!float.TryParse(
            dialog.KeyTimeText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var time
        ))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Key Time은 float 숫자여야 합니다.",
                "Key Time 추가 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        var canonical = FormatFloat(time);
        if (_keyTimes.Contains(canonical, StringComparer.Ordinal))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"이미 존재하는 Key Time입니다: {canonical}",
                "Key Time 추가 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        PushHistory();

        _keyTimes.Add(canonical);
        _keyTimes.Sort((a, b) =>
            float.Parse(a, CultureInfo.InvariantCulture)
                .CompareTo(float.Parse(b, CultureInfo.InvariantCulture)));

        var newColumn = _table.Columns.Add(canonical, typeof(object));
        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState != DataRowState.Deleted)
                row[canonical] = "0";
        }

        // Name은 0번, Key Time은 숫자 오름차순으로 유지한다.
        newColumn.SetOrdinal(_keyTimes.IndexOf(canonical) + 1);
        BuildGridColumns();
        SetDirty(true);
    }

    private void DeleteCurrentKeyTime()
    {
        if (_grid.CurrentCell.Column is null)
            return;

        var columnIndex = _grid.Columns.IndexOf(_grid.CurrentCell.Column);
        if (columnIndex <= 0 || columnIndex - 1 >= _keyTimes.Count)
            return;

        var keyTime = _keyTimes[columnIndex - 1];

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Key Time {keyTime} 열과 모든 Row의 해당 값을 삭제할까요?",
            "Key Time 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (answer != MessageBoxResult.Yes)
            return;

        PushHistory();

        if (_table.Columns.Contains(keyTime))
            _table.Columns.Remove(keyTime);

        _keyTimes.Remove(keyTime);
        BuildGridColumns();
        SetDirty(true);
    }

    private void Grid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_internalChange || _pendingSnapshot is not null)
            return;

        _pendingSnapshot = SerializeDocument();
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_internalChange)
            return;

        Dispatcher.BeginInvoke(
            FinishPendingEdit,
            DispatcherPriority.Background
        );
    }

    private void Grid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (_internalChange)
            return;

        Dispatcher.BeginInvoke(
            FinishPendingEdit,
            DispatcherPriority.Background
        );
    }

    private void FinishPendingEdit()
    {
        if (_pendingSnapshot is null)
            return;

        _history.PushBeforeChange(_pendingSnapshot);
        _pendingSnapshot = null;
        UpdateDirtyFromSnapshot();
        RefreshCurveSelectorAndGraph(fitGraph: false);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete &&
            !_grid.IsReadOnly &&
            _grid.CurrentCell.IsValid)
        {
            ClearSelectedCells();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            MoveCurrentCell(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1,
                0
            );
            e.Handled = true;
        }
    }

    private void ClearSelectedCells()
    {
        if (_grid.SelectedCells.Count == 0)
            return;

        PushHistory();

        foreach (var cell in _grid.SelectedCells)
        {
            if (cell.Item is not DataRowView row)
                continue;

            var index = _grid.Columns.IndexOf(cell.Column);
            if (index < 0)
                continue;

            var key = index == 0
                ? "Name"
                : _keyTimes[index - 1];

            row[key] = string.Empty;
        }

        SetDirty(true);
        RefreshCurveSelectorAndGraph(fitGraph: false);
    }

    private void PasteClipboard()
    {
        if (!Clipboard.ContainsText() ||
            _grid.CurrentItem is not DataRowView currentRow ||
            _grid.CurrentCell.Column is null)
        {
            return;
        }

        var text = Clipboard.GetText()
            .Replace("\r\n", "\n")
            .TrimEnd('\n');

        var matrix = text
            .Split('\n')
            .Select(x => x.Split('\t'))
            .ToArray();

        if (matrix.Length == 0)
            return;

        var rowViews = _table.DefaultView.Cast<DataRowView>().ToList();
        var startRow = rowViews.IndexOf(currentRow);
        var startCol = _grid.Columns.IndexOf(_grid.CurrentCell.Column);
        if (startRow < 0 || startCol < 0)
            return;

        PushHistory();
        _internalChange = true;
        try
        {
            while (_table.Rows.Count < startRow + matrix.Length)
                _table.Rows.Add(_table.NewRow());

            for (var r = 0; r < matrix.Length; r++)
            {
                for (var c = 0;
                     c < matrix[r].Length &&
                     startCol + c < _grid.Columns.Count;
                     c++)
                {
                    var columnIndex = startCol + c;
                    var key = columnIndex == 0
                        ? "Name"
                        : _keyTimes[columnIndex - 1];

                    _table.Rows[startRow + r][key] = matrix[r][c];
                }
            }
        }
        finally
        {
            _internalChange = false;
        }

        SetDirty(true);
        RefreshCurveSelectorAndGraph(fitGraph: false);
    }

    private void MoveCurrentCell(int rowDelta, int colDelta)
    {
        if (_grid.CurrentItem is not DataRowView row ||
            _grid.CurrentCell.Column is null)
        {
            return;
        }

        var rowViews = _table.DefaultView.Cast<DataRowView>().ToList();
        if (rowViews.Count == 0)
            return;

        var rowIndex = rowViews.IndexOf(row);
        var colIndex = _grid.Columns.IndexOf(_grid.CurrentCell.Column);

        var targetRow = Math.Clamp(
            rowIndex + rowDelta,
            0,
            rowViews.Count - 1
        );

        var targetCol = Math.Clamp(
            colIndex + colDelta,
            0,
            Math.Max(0, _grid.Columns.Count - 1)
        );

        _grid.CurrentCell =
            new DataGridCellInfo(
                rowViews[targetRow],
                _grid.Columns[targetCol]
            );

        _grid.ScrollIntoView(
            rowViews[targetRow],
            _grid.Columns[targetCol]
        );
    }

    private string GetInterpModeDisplayName() =>
        (Entry.InterpMode ?? 0) switch
        {
            1 => "Constant",
            2 => "Cubic / Auto Tangent",
            _ => "Linear"
        };

    private void RefreshCurveSelectorAndGraph(bool fitGraph)
    {
        if (_syncingCurveSelection)
            return;

        _syncingCurveSelection = true;
        try
        {
            var previousRow =
                (_curveSelector.SelectedItem as ComboBoxItem)?.Tag as DataRowView;

            _curveSelector.Items.Clear();

            ComboBoxItem? itemToSelect = null;
            foreach (DataRowView rowView in _table.DefaultView)
            {
                var name = Convert.ToString(rowView["Name"])?.Trim();
                var item = new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(name)
                        ? "<이름 없음>"
                        : name,
                    Tag = rowView
                };

                _curveSelector.Items.Add(item);

                if (ReferenceEquals(rowView, previousRow))
                    itemToSelect = item;
            }

            if (itemToSelect is not null)
            {
                _curveSelector.SelectedItem = itemToSelect;
            }
            else if (_grid.CurrentItem is DataRowView current)
            {
                foreach (var item in _curveSelector.Items.OfType<ComboBoxItem>())
                {
                    if (ReferenceEquals(item.Tag, current))
                    {
                        _curveSelector.SelectedItem = item;
                        break;
                    }
                }
            }

            if (_curveSelector.SelectedItem is null &&
                _curveSelector.Items.Count > 0)
            {
                _curveSelector.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncingCurveSelection = false;
        }

        RefreshGraph(fitGraph);
    }

    private DataRowView? GetActiveCurveRow() =>
        (_curveSelector.SelectedItem as ComboBoxItem)?.Tag as DataRowView;

    private void RefreshGraph(bool fitGraph)
    {
        var row = GetActiveCurveRow();
        if (row is null)
        {
            _graph.SetPoints([], fitGraph);
            ClearGraphKeySelection();
            return;
        }

        var points = new List<CurveGraphPoint>();

        foreach (var keyTime in _keyTimes)
        {
            var valueText = Convert.ToString(row[keyTime])?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(valueText))
                continue;

            if (!float.TryParse(
                keyTime,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var time
            ))
            {
                continue;
            }

            if (!float.TryParse(
                valueText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            ))
            {
                continue;
            }

            points.Add(new CurveGraphPoint(time, value));
        }

        points.Sort((a, b) => a.Time.CompareTo(b.Time));
        _graph.InterpMode = Entry.InterpMode ?? 0;
        _graph.SetPoints(points, fitGraph);

        if (_selectedGraphTime.HasValue)
        {
            var matching = points.FirstOrDefault(
                x => NearlyEqual(x.Time, _selectedGraphTime.Value)
            );

            if (matching is not null)
            {
                _graph.SelectTime(matching.Time);
                UpdateGraphKeySelection(matching.Time, matching.Value);
                return;
            }
        }

        ClearGraphKeySelection();
    }

    private void CurveSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (_syncingCurveSelection)
            return;

        if (GetActiveCurveRow() is { } row)
        {
            _syncingCurveSelection = true;
            try
            {
                _grid.SelectedItem = row;
                _grid.ScrollIntoView(row);
            }
            finally
            {
                _syncingCurveSelection = false;
            }
        }

        _selectedGraphTime = null;
        RefreshGraph(fitGraph: true);
    }

    private void Grid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        SyncCurveSelectorFromGrid();
    }

    private void Grid_CurrentCellChanged(object? sender, EventArgs e)
    {
        SyncCurveSelectorFromGrid();
    }

    private void SyncCurveSelectorFromGrid()
    {
        if (_syncingCurveSelection ||
            _grid.CurrentItem is not DataRowView row)
        {
            return;
        }

        foreach (var item in _curveSelector.Items.OfType<ComboBoxItem>())
        {
            if (!ReferenceEquals(item.Tag, row))
                continue;

            if (!ReferenceEquals(_curveSelector.SelectedItem, item))
            {
                _syncingCurveSelection = true;
                try
                {
                    _curveSelector.SelectedItem = item;
                }
                finally
                {
                    _syncingCurveSelection = false;
                }

                _selectedGraphTime = null;
                RefreshGraph(fitGraph: true);
            }

            break;
        }
    }

    private void Graph_KeySelected(
        object? sender,
        CurveGraphKeySelectedEventArgs e
    )
    {
        _selectedGraphTime = e.Time;
        UpdateGraphKeySelection(e.Time, e.Value);
    }

    private void UpdateGraphKeySelection(float time, float value)
    {
        _selectedGraphTime = time;
        _selectedKeyText.Text = "선택 Key";
        _selectedTimeBox.IsEnabled = true;
        _selectedValueBox.IsEnabled = true;
        _selectedTimeBox.Text = FormatFloat(time);
        _selectedValueBox.Text = FormatFloat(value);
    }

    private void ClearGraphKeySelection()
    {
        _selectedGraphTime = null;
        _selectedKeyText.Text = "선택 Key: 없음";
        _selectedTimeBox.Text = string.Empty;
        _selectedValueBox.Text = string.Empty;
        _selectedTimeBox.IsEnabled = false;
        _selectedValueBox.IsEnabled = false;
    }

    private void Graph_KeyMoveCommitted(
        object? sender,
        CurveGraphKeyMoveCommittedEventArgs e
    )
    {
        MoveActiveCurveKey(
            e.OriginalTime,
            e.NewTime,
            e.NewValue
        );
    }

    private void Graph_AddKeyRequested(
        object? sender,
        CurveGraphAddKeyRequestedEventArgs e
    )
    {
        AddKeyToActiveCurve(e.Time, e.Value);
    }

    private void AddKeyToActiveCurve()
    {
        var row = GetActiveCurveRow();
        if (row is null)
        {
            AddRow();
            row = GetActiveCurveRow();
        }

        if (row is null)
            return;

        float suggestedTime = 0.0f;
        float suggestedValue = 0.0f;

        var existing = GetActiveCurvePoints();
        if (existing.Count > 0)
        {
            suggestedTime = existing[^1].Time + 1.0f;
            suggestedValue = existing[^1].Value;
        }

        var dialog = new CurveKeyValueDialog(
            suggestedTime,
            suggestedValue
        )
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
            return;

        if (!float.TryParse(
            dialog.KeyTimeText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var time
        ) ||
            !float.TryParse(
                dialog.ValueText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            ))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Time과 Value는 float 숫자여야 합니다.",
                "Key 추가 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        AddKeyToActiveCurve(time, value);
    }

    private void AddKeyToActiveCurve(float time, float value)
    {
        var row = GetActiveCurveRow();
        if (row is null)
            return;

        var key = FormatFloat(time);

        if (_table.Columns.Contains(key))
        {
            var existingValue = Convert.ToString(row[key])?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(existingValue))
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"현재 Curve에 Time {key} Key가 이미 존재합니다.",
                    "Key 추가 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }
        }

        PushHistory();
        EnsureKeyColumn(key);
        row[key] = FormatFloat(value);
        SortKeyColumns();

        _selectedGraphTime = time;
        SetDirty(true);
        RefreshCurveSelectorAndGraph(fitGraph: false);
        _graph.SelectTime(time);
    }

    private void DeleteSelectedGraphKey()
    {
        var row = GetActiveCurveRow();
        if (row is null || !_selectedGraphTime.HasValue)
            return;

        var key = FindExistingKey(_selectedGraphTime.Value);
        if (key is null || !_table.Columns.Contains(key))
            return;

        PushHistory();
        row[key] = string.Empty;
        RemoveKeyColumnIfUnused(key);

        SetDirty(true);
        ClearGraphKeySelection();
        RefreshCurveSelectorAndGraph(fitGraph: false);
    }

    private void ApplySelectedKeyNumericValues()
    {
        if (!_selectedGraphTime.HasValue)
            return;

        if (!float.TryParse(
            _selectedTimeBox.Text.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var newTime
        ) ||
            !float.TryParse(
                _selectedValueBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var newValue
        ))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Time과 Value는 float 숫자여야 합니다.",
                "Key 수정 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        MoveActiveCurveKey(
            _selectedGraphTime.Value,
            newTime,
            newValue
        );
    }

    private void MoveActiveCurveKey(
        float originalTime,
        float newTime,
        float newValue
    )
    {
        var row = GetActiveCurveRow();
        if (row is null)
            return;

        var oldKey = FindExistingKey(originalTime);
        if (oldKey is null || !_table.Columns.Contains(oldKey))
            return;

        var newKey = FormatFloat(newTime);

        if (!oldKey.Equals(newKey, StringComparison.Ordinal) &&
            _table.Columns.Contains(newKey))
        {
            var collision = Convert.ToString(row[newKey])?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(collision))
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"현재 Curve에 Time {newKey} Key가 이미 존재합니다.",
                    "Key 이동 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );

                RefreshGraph(fitGraph: false);
                return;
            }
        }

        PushHistory();

        EnsureKeyColumn(newKey);
        row[newKey] = FormatFloat(newValue);

        if (!oldKey.Equals(newKey, StringComparison.Ordinal))
        {
            row[oldKey] = string.Empty;
            RemoveKeyColumnIfUnused(oldKey);
        }

        SortKeyColumns();
        _selectedGraphTime = newTime;
        SetDirty(true);
        RefreshCurveSelectorAndGraph(fitGraph: false);
        _graph.SelectTime(newTime);
    }

    private List<CurveGraphPoint> GetActiveCurvePoints()
    {
        var result = new List<CurveGraphPoint>();
        var row = GetActiveCurveRow();
        if (row is null)
            return result;

        foreach (var key in _keyTimes)
        {
            if (!float.TryParse(
                key,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var time
            ))
            {
                continue;
            }

            var valueText = Convert.ToString(row[key])?.Trim() ?? string.Empty;
            if (!float.TryParse(
                valueText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            ))
            {
                continue;
            }

            result.Add(new CurveGraphPoint(time, value));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private string? FindExistingKey(float time)
    {
        foreach (var key in _keyTimes)
        {
            if (!float.TryParse(
                key,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            ))
            {
                continue;
            }

            if (NearlyEqual(parsed, time))
                return key;
        }

        return null;
    }

    private void EnsureKeyColumn(string key)
    {
        if (!_keyTimes.Contains(key, StringComparer.Ordinal))
            _keyTimes.Add(key);

        if (!_table.Columns.Contains(key))
        {
            var column = _table.Columns.Add(key, typeof(object));
            foreach (DataRow dataRow in _table.Rows)
            {
                if (dataRow.RowState != DataRowState.Deleted)
                    dataRow[key] = string.Empty;
            }
        }
    }

    private void RemoveKeyColumnIfUnused(string key)
    {
        if (!_table.Columns.Contains(key))
            return;

        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted)
                continue;

            var value = Convert.ToString(row[key])?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                return;
        }

        _table.Columns.Remove(key);
        _keyTimes.Remove(key);
    }

    private void SortKeyColumns()
    {
        _keyTimes.Sort((a, b) =>
        {
            var av = float.Parse(a, CultureInfo.InvariantCulture);
            var bv = float.Parse(b, CultureInfo.InvariantCulture);
            return av.CompareTo(bv);
        });

        for (var i = 0; i < _keyTimes.Count; i++)
        {
            if (_table.Columns.Contains(_keyTimes[i]))
                _table.Columns[_keyTimes[i]]!.SetOrdinal(i + 1);
        }

        BuildGridColumns();
    }

    private static bool NearlyEqual(float a, float b)
    {
        var scale = Math.Max(1.0f, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-5f * scale;
    }

    private string MakeUniqueRowName()
    {
        var existing = _table.Rows.Cast<DataRow>()
            .Where(x => x.RowState != DataRowState.Deleted)
            .Select(x => Convert.ToString(x["Name"]) ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; ; i++)
        {
            var candidate = $"NewCurve_{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    private void PushHistory()
    {
        if (_internalChange)
            return;

        _history.PushBeforeChange(SerializeDocument());
    }

    private void SetDirty(bool dirty)
    {
        if (_isDirty == dirty)
            return;

        _isDirty = dirty;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirtyFromSnapshot() =>
        SetDirty(SerializeDocument() != _savedSnapshot);

    private static bool IsEmptyNewRow(DataRow row)
    {
        if (row.RowState == DataRowState.Deleted)
            return true;

        return row.ItemArray.All(
            x => x == DBNull.Value ||
                 string.IsNullOrWhiteSpace(Convert.ToString(x))
        );
    }

    private static bool TryReadFloat(JsonNode? node, out float value)
    {
        value = 0.0f;
        try
        {
            if (node is not JsonValue jsonValue)
                return false;

            if (jsonValue.TryGetValue<float>(out value))
                return true;

            if (jsonValue.TryGetValue<double>(out var d))
            {
                value = (float)d;
                return true;
            }

            if (jsonValue.TryGetValue<int>(out var i))
            {
                value = i;
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var l))
            {
                value = l;
                return true;
            }
        }
        catch
        {
            // 아래 false 반환으로 처리한다.
        }

        return false;
    }

    private static string FormatFloat(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

/** Key Time 숫자를 입력받는 작은 Dialog다. */
internal sealed class CurveKeyTimeDialog : Window
{
    private readonly TextBox _textBox = new();

    public string KeyTimeText => _textBox.Text.Trim();

    public CurveKeyTimeDialog()
    {
        Title = "Curve Key Time 추가";
        Width = 360;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel
        {
            Margin = new Thickness(12)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var ok = new Button
        {
            Content = "확인",
            IsDefault = true,
            MinWidth = 70
        };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = "취소",
            IsCancel = true,
            MinWidth = 70
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new TextBlock
        {
            Text = "추가할 Key Time을 입력하세요. (예: 0.2, 1, 5.5)",
            Margin = new Thickness(0, 0, 0, 6)
        });

        _textBox.MinWidth = 200;
        root.Children.Add(_textBox);

        Content = root;
    }
}


/** Curve Key의 Time/Value를 함께 입력받는 Dialog다. */
internal sealed class CurveKeyValueDialog : Window
{
    private readonly TextBox _timeBox = new();
    private readonly TextBox _valueBox = new();

    public string KeyTimeText => _timeBox.Text.Trim();
    public string ValueText => _valueBox.Text.Trim();

    public CurveKeyValueDialog(float suggestedTime, float suggestedValue)
    {
        Title = "Curve Key 추가";
        Width = 380;
        Height = 205;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(12)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var description = new TextBlock
        {
            Text = "추가할 Key의 Time과 Value를 입력하세요.",
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetColumnSpan(description, 2);
        Grid.SetRow(description, 0);
        root.Children.Add(description);

        var timeLabel = new TextBlock
        {
            Text = "Time",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(timeLabel, 1);
        root.Children.Add(timeLabel);

        _timeBox.Text = suggestedTime.ToString("R", CultureInfo.InvariantCulture);
        _timeBox.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_timeBox, 1);
        Grid.SetColumn(_timeBox, 1);
        root.Children.Add(_timeBox);

        var valueLabel = new TextBlock
        {
            Text = "Value",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(valueLabel, 2);
        root.Children.Add(valueLabel);

        _valueBox.Text = suggestedValue.ToString("R", CultureInfo.InvariantCulture);
        _valueBox.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_valueBox, 2);
        Grid.SetColumn(_valueBox, 1);
        root.Children.Add(_valueBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var ok = new Button
        {
            Content = "확인",
            IsDefault = true,
            MinWidth = 70
        };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = "취소",
            IsCancel = true,
            MinWidth = 70,
            Margin = new Thickness(4, 0, 0, 0)
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        Grid.SetRow(buttons, 3);
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
