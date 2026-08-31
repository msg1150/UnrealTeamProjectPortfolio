using JsonAssetDataEditor.Core;
using System.IO; // Path/File/Directory 및 IO 예외 형식을 사용한다.
using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JsonAssetDataEditor.Editors;

public sealed class DataTableEditorControl : UserControl, IDataEditor
{
    /*
     * DataTable의 Array/Map/Set 같은 복합 값을 셀과 편집창에 보여줄 때 사용하는 옵션이다.
     *
     * System.Text.Json 기본 Encoder는 한글을 \uXXXX 형태로 이스케이프할 수 있다.
     * 저장 데이터의 의미는 같지만 기획자가 읽기 불편하므로,
     * UI 표시용 문자열에서는 모든 Unicode 문자를 그대로 표시한다.
     *
     * 실제 DataTable JSON 저장 규격은 SerializeDocument()의 기존 옵션을 그대로 사용한다.
     */
    private static readonly JsonSerializerOptions CompactDisplayJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly JsonSerializerOptions PrettyDisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private sealed record ColumnSpec(
        string Key,
        string Header,
        IReadOnlyList<string> JsonPath,
        PropertySchema? Schema,
        bool IsName,
        bool IsComplex);

    private readonly DataGrid _grid = new();
    private readonly TextBlock _hint = new();
    private readonly SnapshotHistory _history = new();
    private readonly List<ColumnSpec> _columns = [];
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

    public DataTableEditorControl(ManifestEntry entry, string jsonText)
    {
        Entry = entry;
        BuildColumnSpecs();
        BuildUi();
        LoadSerializedDocument(jsonText, resetHistory: true);
    }

    private void BuildUi()
    {
        var root = new DockPanel();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 8, 4)
        };

        var addRow = new Button { Content = "행 추가" };
        addRow.Click += (_, _) => AddRow();
        var deleteRow = new Button { Content = "선택 행 삭제" };
        deleteRow.Click += (_, _) => DeleteSelectedRows();

        _hint.Text = "방향키/Enter/Tab 이동 · Ctrl+C/V · F2 편집 · 복합 값은 더블클릭";
        _hint.Foreground = Brushes.Gray;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(12, 0, 0, 0);

        toolbar.Children.Add(addRow);
        toolbar.Children.Add(deleteRow);
        toolbar.Children.Add(_hint);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        _grid.Margin = new Thickness(8);
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
        _grid.MouseDoubleClick += Grid_MouseDoubleClick;

        root.Children.Add(_grid);
        Content = root;
    }

    private void BuildColumnSpecs()
    {
        _columns.Clear();
        _columns.Add(new ColumnSpec("Name", "Name", ["Name"], null, true, false));

        foreach (var schema in Entry.Properties.OrderBy(x => x.Order))
            FlattenSchema(schema, [], _columns);
    }

    private static void FlattenSchema(PropertySchema schema, List<string> parentPath, List<ColumnSpec> output)
    {
        var path = new List<string>(parentPath) { schema.Name };

        if (schema.Type == "struct" && schema.Properties.Count > 0)
        {
            foreach (var child in schema.Properties.OrderBy(x => x.Order))
                FlattenSchema(child, path, output);
            return;
        }

        var key = string.Join('.', path);
        var header = key;
        output.Add(new ColumnSpec(key, header, path, schema, false, schema.IsContainer || schema.Type == "gameplayTagContainer"));
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Clear();

        foreach (var column in _columns)
        {
            DataGridColumn dataGridColumn;

            if (column.Schema?.Type == "bool")
            {
                dataGridColumn = new DataGridCheckBoxColumn
                {
                    Header = column.Header,
                    Binding = new Binding($"[{column.Key}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    }
                };
            }
            else if (column.Schema?.Type == "enum")
            {
                dataGridColumn = new DataGridComboBoxColumn
                {
                    Header = column.Header,
                    ItemsSource = column.Schema.EnumValues.Select(x => x.Name).ToList(),
                    SelectedItemBinding = new Binding($"[{column.Key}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    }
                };
            }
            else
            {
                dataGridColumn = new DataGridTextColumn
                {
                    Header = column.IsComplex ? column.Header + " …" : column.Header,
                    Binding = new Binding($"[{column.Key}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                    },
                    IsReadOnly = false
                };
            }

            dataGridColumn.MinWidth = column.IsName ? 140 : 90;
            dataGridColumn.Width = DataGridLength.SizeToHeader;
            _grid.Columns.Add(dataGridColumn);
        }
    }

    public void LoadSerializedDocument(string json, bool resetHistory)
    {
        _internalChange = true;
        try
        {
            var root = JsonNode.Parse(json) as JsonArray
                       ?? throw new InvalidDataException("DataTable JSON 최상위는 Array여야 합니다.");

            var table = new DataTable();
            foreach (var column in _columns)
                table.Columns.Add(column.Key, typeof(object));

            foreach (var item in root)
            {
                if (item is not JsonObject obj) continue;
                var row = table.NewRow();

                foreach (var column in _columns)
                {
                    JsonNode? value = column.IsName
                        ? obj["Name"]
                        : GetAtPath(obj, column.JsonPath);

                    if (column.Schema?.Type == "bool" && value is JsonValue boolValue && boolValue.TryGetValue<bool>(out var b))
                        row[column.Key] = b;
                    else if (column.IsComplex)
                        row[column.Key] = value?.ToJsonString(CompactDisplayJsonOptions) ?? string.Empty;
                    else
                        row[column.Key] = SchemaValueService.NodeToEditorText(value, column.Schema);
                }

                table.Rows.Add(row);
            }

            _table = table;
            _grid.ItemsSource = _table.DefaultView;
            BuildGridColumns();

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

    public string SerializeDocument(bool indented = true)
    {
        var array = new JsonArray();

        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            if (IsEmptyNewRow(row)) continue;

            var obj = new JsonObject();
            foreach (var column in _columns)
            {
                var raw = row[column.Key] == DBNull.Value ? null : row[column.Key];
                JsonNode? value;

                if (column.IsName)
                {
                    value = JsonValue.Create(Convert.ToString(raw) ?? string.Empty);
                }
                else if (column.IsComplex)
                {
                    var text = Convert.ToString(raw) ?? string.Empty;
                    value = string.IsNullOrWhiteSpace(text)
                        ? SchemaValueService.CreateDefault(column.Schema!)
                        : JsonNode.Parse(text);
                }
                else if (column.Schema?.Type == "bool")
                {
                    value = JsonValue.Create(raw is bool b && b);
                }
                else if (column.Schema?.Type == "enum")
                {
                    value = JsonValue.Create(Convert.ToString(raw) ?? string.Empty);
                }
                else
                {
                    var parsed = SchemaValueService.ParseEditorText(Convert.ToString(raw) ?? string.Empty, column.Schema!, out _);
                    value = parsed ?? JsonValue.Create(Convert.ToString(raw) ?? string.Empty);
                }

                if (column.IsName)
                    obj["Name"] = value;
                else
                    SetAtPath(obj, column.JsonPath, value);
            }
            array.Add(obj);
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    public IReadOnlyList<ValidationIssue> ValidateDocument()
    {
        try
        {
            var node = JsonNode.Parse(SerializeDocument());
            return SchemaValueService.ValidateEntry(node, Entry);
        }
        catch (Exception ex)
        {
            return [new ValidationIssue(string.Empty, ex.Message)];
        }
    }

    public void MarkSaved()
    {
        _savedSnapshot = SerializeDocument();
        SetDirty(false);
    }

    public void Undo()
    {
        var snapshot = _history.Undo(SerializeDocument());
        if (snapshot is not null) LoadSerializedDocument(snapshot, resetHistory: false);
    }

    public void Redo()
    {
        var snapshot = _history.Redo(SerializeDocument());
        if (snapshot is not null) LoadSerializedDocument(snapshot, resetHistory: false);
    }

    public void ImportCsv(string csvText)
    {
        var rows = CsvCodec.Parse(csvText);
        if (rows.Count == 0) throw new InvalidDataException("CSV가 비어 있습니다.");

        var headers = rows[0];
        var headerMap = new Dictionary<int, ColumnSpec>();
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i].Trim();
            var column = _columns.FirstOrDefault(x => x.Key.Equals(header, StringComparison.OrdinalIgnoreCase));
            if (column is null)
                throw new InvalidDataException($"현재 Schema에 없는 CSV 열입니다: {header}");
            headerMap[i] = column;
        }

        var required = _columns.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provided = headerMap.Values.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(x => !provided.Contains(x)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException("CSV에 필요한 열이 없습니다: " + string.Join(", ", missing));

        PushHistory();
        _internalChange = true;
        try
        {
            _table.Rows.Clear();
            foreach (var fields in rows.Skip(1))
            {
                if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])) continue;
                var row = _table.NewRow();
                foreach (var pair in headerMap)
                {
                    var text = pair.Key < fields.Count ? fields[pair.Key] : string.Empty;
                    row[pair.Value.Key] = pair.Value.Schema?.Type == "bool"
                        ? bool.TryParse(text, out var b) && b
                        : text;
                }
                _table.Rows.Add(row);
            }
        }
        finally
        {
            _internalChange = false;
        }
        SetDirty(true);
    }

    public string ExportCsv()
    {
        var rows = new List<List<string?>>
        {
            _columns.Select(x => (string?)x.Key).ToList()
        };

        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted || IsEmptyNewRow(row)) continue;
            rows.Add(_columns.Select(x => row[x.Key] == DBNull.Value ? string.Empty : Convert.ToString(row[x.Key])).ToList());
        }

        return CsvCodec.Write(rows);
    }

    public void ExpandAll() { }
    public void CollapseAll() { }

    private void Grid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_internalChange) return;
        _pendingSnapshot = SerializeDocument();
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_internalChange) return;
        Dispatcher.BeginInvoke(() => FinishPendingEdit(), DispatcherPriority.Background);
    }

    private void Grid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (_internalChange) return;
        Dispatcher.BeginInvoke(() => FinishPendingEdit(), DispatcherPriority.Background);
    }

    private void FinishPendingEdit()
    {
        if (_pendingSnapshot is null) return;
        _history.PushBeforeChange(_pendingSnapshot);
        _pendingSnapshot = null;
        UpdateDirtyFromSnapshot();
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && !_grid.IsReadOnly && _grid.CurrentCell.IsValid)
        {
            ClearSelectedCells();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            MoveCurrentCell(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1, 0);
            e.Handled = true;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_grid.CurrentCell.Column is null || _grid.CurrentItem is not DataRowView rowView) return;
        var columnIndex = _grid.Columns.IndexOf(_grid.CurrentCell.Column);
        if (columnIndex < 0 || columnIndex >= _columns.Count) return;
        var spec = _columns[columnIndex];
        if (!spec.IsComplex || spec.Schema is null) return;

        var current = Convert.ToString(rowView[spec.Key]);
        var dialog = new ComplexJsonDialog(spec.Schema, current)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        PushHistory();
        rowView[spec.Key] = dialog.ResultJson;
        SetDirty(true);
    }

    private void AddRow()
    {
        PushHistory();
        var row = _table.NewRow();
        foreach (var column in _columns)
        {
            if (column.IsName) row[column.Key] = MakeUniqueRowName();
            else if (column.Schema?.Type == "bool") row[column.Key] = false;
            else if (column.IsComplex) row[column.Key] = SchemaValueService.CreateDefault(column.Schema!)?.ToJsonString(CompactDisplayJsonOptions) ?? string.Empty;
            else row[column.Key] = SchemaValueService.NodeToEditorText(SchemaValueService.CreateDefault(column.Schema!), column.Schema);
        }
        _table.Rows.Add(row);
        SetDirty(true);
        _grid.SelectedItem = _table.DefaultView[_table.DefaultView.Count - 1];
        _grid.ScrollIntoView(_grid.SelectedItem);
    }

    private void DeleteSelectedRows()
    {
        var rows = _grid.SelectedCells
            .Select(x => x.Item)
            .OfType<DataRowView>()
            .Distinct()
            .ToList();
        if (rows.Count == 0 && _grid.CurrentItem is DataRowView current) rows.Add(current);
        if (rows.Count == 0) return;

        PushHistory();
        foreach (var row in rows) row.Delete();
        SetDirty(true);
    }

    private void ClearSelectedCells()
    {
        if (_grid.SelectedCells.Count == 0) return;
        PushHistory();
        foreach (var cell in _grid.SelectedCells)
        {
            if (cell.Item is not DataRowView row) continue;
            var index = _grid.Columns.IndexOf(cell.Column);
            if (index < 0 || index >= _columns.Count) continue;
            var column = _columns[index];
            row[column.Key] = column.Schema?.Type == "bool" ? false : string.Empty;
        }
        SetDirty(true);
    }

    private void PasteClipboard()
    {
        if (!Clipboard.ContainsText() || _grid.CurrentItem is not DataRowView currentRow || _grid.CurrentCell.Column is null)
            return;

        var text = Clipboard.GetText().Replace("\r\n", "\n").TrimEnd('\n');
        var matrix = text.Split('\n').Select(x => x.Split('\t')).ToArray();
        if (matrix.Length == 0) return;

        var startRow = _table.DefaultView.Cast<DataRowView>().ToList().IndexOf(currentRow);
        var startCol = _grid.Columns.IndexOf(_grid.CurrentCell.Column);
        if (startRow < 0 || startCol < 0) return;

        PushHistory();
        _internalChange = true;
        try
        {
            while (_table.Rows.Count < startRow + matrix.Length)
                _table.Rows.Add(_table.NewRow());

            for (var r = 0; r < matrix.Length; r++)
            {
                for (var c = 0; c < matrix[r].Length && startCol + c < _columns.Count; c++)
                {
                    var spec = _columns[startCol + c];
                    var value = matrix[r][c];
                    _table.Rows[startRow + r][spec.Key] = spec.Schema?.Type == "bool"
                        ? bool.TryParse(value, out var b) && b
                        : value;
                }
            }
        }
        finally
        {
            _internalChange = false;
        }
        SetDirty(true);
    }

    private void MoveCurrentCell(int rowDelta, int colDelta)
    {
        if (_grid.CurrentItem is not DataRowView row || _grid.CurrentCell.Column is null) return;
        var rowViews = _table.DefaultView.Cast<DataRowView>().ToList();
        var rowIndex = rowViews.IndexOf(row);
        var colIndex = _grid.Columns.IndexOf(_grid.CurrentCell.Column);
        var targetRow = Math.Clamp(rowIndex + rowDelta, 0, Math.Max(0, rowViews.Count - 1));
        var targetCol = Math.Clamp(colIndex + colDelta, 0, Math.Max(0, _grid.Columns.Count - 1));
        if (rowViews.Count == 0) return;

        _grid.CurrentCell = new DataGridCellInfo(rowViews[targetRow], _grid.Columns[targetCol]);
        _grid.ScrollIntoView(rowViews[targetRow], _grid.Columns[targetCol]);
    }

    private string MakeUniqueRowName()
    {
        var existing = _table.Rows.Cast<DataRow>()
            .Where(x => x.RowState != DataRowState.Deleted)
            .Select(x => Convert.ToString(x["Name"]) ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; ; i++)
        {
            var candidate = $"NewRow_{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private void PushHistory()
    {
        if (_internalChange) return;
        _history.PushBeforeChange(SerializeDocument());
    }

    private void SetDirty(bool dirty)
    {
        if (_isDirty == dirty) return;
        _isDirty = dirty;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirtyFromSnapshot() => SetDirty(SerializeDocument() != _savedSnapshot);

    private static bool IsEmptyNewRow(DataRow row)
    {
        if (row.RowState == DataRowState.Deleted) return true;
        return row.ItemArray.All(x => x == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(x)));
    }

    private static JsonNode? GetAtPath(JsonObject root, IReadOnlyList<string> path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
        {
            if (node is not JsonObject obj || !SchemaValueService.TryGetCaseInsensitive(obj, segment, out node))
                return null;
        }
        return node;
    }

    private static void SetAtPath(JsonObject root, IReadOnlyList<string> path, JsonNode? value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (current[path[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[path[i]] = child;
            }
            current = child;
        }
        current[path[^1]] = value?.DeepClone();
    }
}

internal sealed class ComplexJsonDialog : Window
{
    /*
     * ComplexJsonDialog는 DataTableEditorControl과 별도 클래스이므로
     * DataTableEditorControl의 private 표시 옵션에 접근할 수 없다.
     *
     * 복합값 편집창에서도 한글을 \uXXXX가 아닌 실제 문자로 표시하기 위해
     * 동일한 UI 표시용 JSON 옵션을 이 클래스에도 둔다.
     */
    private static readonly JsonSerializerOptions CompactDisplayJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly JsonSerializerOptions PrettyDisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly PropertySchema _schema;
    private readonly TextBox _editor = new();
    private readonly TextBlock _error = new();

    public string ResultJson { get; private set; } = string.Empty;

    public ComplexJsonDialog(PropertySchema schema, string? currentJson)
    {
        _schema = schema;
        Title = $"{schema.EffectiveDisplayName} 편집";
        Width = 700;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "확인", IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "취소", IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _error.Foreground = Brushes.Firebrick;
        _error.Margin = new Thickness(0, 4, 0, 4);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(_error);

        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.FontFamily = new FontFamily("Consolas");
        _editor.Text = Pretty(currentJson);
        root.Children.Add(_editor);
        Content = root;
    }

    private void Accept()
    {
        try
        {
            var node = string.IsNullOrWhiteSpace(_editor.Text)
                ? SchemaValueService.CreateDefault(_schema)
                : JsonNode.Parse(_editor.Text);
            var issues = new List<ValidationIssue>();
            SchemaValueService.ValidateNode(node, _schema, _schema.Name, issues);
            if (issues.Count > 0)
            {
                _error.Text = issues[0].ToString();
                return;
            }

            ResultJson = node?.ToJsonString(CompactDisplayJsonOptions) ?? "null";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
    }

    private static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try { return JsonNode.Parse(json)?.ToJsonString(PrettyDisplayJsonOptions) ?? string.Empty; }
        catch { return json; }
    }
}
