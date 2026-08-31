using JsonAssetDataEditor.Core;
using System.IO; // Path/File/Directory 및 IO 예외 형식을 사용한다.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JsonAssetDataEditor.Editors;

public sealed class DataAssetEditorControl : UserControl, IDataEditor
{
    private sealed class CategoryNode
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public int Order { get; set; } = int.MaxValue;
        public List<CategoryNode> Children { get; } = [];
        public List<PropertySchema> Properties { get; } = [];
    }

    private readonly UserSettingsStore _settingsStore;
    private readonly UserSettings _settings;
    private readonly SnapshotHistory _history = new();
    private readonly StackPanel _contentPanel = new();
    private readonly TextBox _searchBox = new();
    private readonly List<Expander> _categoryExpanders = [];
    private JsonObject _root = new();
    private bool _rebuilding;
    private bool _isDirty;
    private string _savedSnapshot = string.Empty;

    public ManifestEntry Entry { get; }
    public bool IsDirty => _isDirty;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool SupportsCsv => false;
    public event EventHandler? DirtyStateChanged;

    public DataAssetEditorControl(
        ManifestEntry entry,
        string jsonText,
        UserSettingsStore settingsStore,
        UserSettings settings)
    {
        Entry = entry;
        _settingsStore = settingsStore;
        _settings = settings;
        BuildUi();
        LoadSerializedDocument(jsonText, resetHistory: true);
    }

    private void BuildUi()
    {
        var root = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(10, 8, 10, 4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var expand = new Button { Content = "모두 펼치기" };
        expand.Click += (_, _) => ExpandAll();
        var collapse = new Button { Content = "모두 접기" };
        collapse.Click += (_, _) => CollapseAll();
        buttons.Children.Add(expand);
        buttons.Children.Add(collapse);
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);

        _searchBox.Margin = new Thickness(0, 2, 10, 2);
        _searchBox.ToolTip = "Property 이름, 표시 이름, Category, Tooltip 검색";
        _searchBox.TextChanged += (_, _) => RebuildContent();
        toolbar.Children.Add(_searchBox);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10, 4, 10, 12)
        };
        scroll.Content = _contentPanel;
        root.Children.Add(scroll);
        Content = root;
    }

    public void LoadSerializedDocument(string json, bool resetHistory)
    {
        _rebuilding = true;
        try
        {
            var parsedRoot = JsonNode.Parse(json) as JsonObject
                             ?? throw new InvalidDataException("DataAsset JSON 최상위는 Object여야 합니다.");

            _root = SchemaValueService.NormalizeDataAssetDocument(parsedRoot, Entry);
            var originalSnapshot = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            SchemaValueService.EnsureSchemaDefaults(_root, Entry.Properties);
            RebuildContent();

            if (resetHistory)
            {
                _history.Clear();
                _savedSnapshot = originalSnapshot;
                SetDirty(SerializeDocument() != _savedSnapshot);
            }
            else
            {
                UpdateDirtyFromSnapshot();
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    public string SerializeDocument(bool indented = true) =>
        _root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public IReadOnlyList<ValidationIssue> ValidateDocument() =>
        SchemaValueService.ValidateEntry(_root, Entry);

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

    public void ImportCsv(string csvText) =>
        throw new NotSupportedException("DataAsset은 CSV가 아니라 Property UI에서 직접 편집하고 JSON으로 저장합니다.");

    public string ExportCsv() =>
        throw new NotSupportedException("DataAsset CSV Export는 지원하지 않습니다.");

    public void ExpandAll()
    {
        foreach (var expander in _categoryExpanders)
            expander.IsExpanded = true;

        if (string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            _settings.CollapsedCategories[Entry.Id] = [];
            _settingsStore.Save(_settings);
        }
    }

    public void CollapseAll()
    {
        foreach (var expander in _categoryExpanders)
            expander.IsExpanded = false;

        if (string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            _settings.CollapsedCategories[Entry.Id] =
                _categoryExpanders
                    .Select(x => Convert.ToString(x.Tag) ?? string.Empty)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            _settingsStore.Save(_settings);
        }
    }

    private void RebuildContent()
    {
        if (_root is null) return;

        _rebuilding = true;
        try
        {
            _contentPanel.Children.Clear();
            _categoryExpanders.Clear();

            var search = _searchBox.Text.Trim();
            var tree = BuildCategoryTree(Entry.Properties);

            foreach (var node in tree.Children.OrderBy(x => x.Order))
            {
                if (!CategoryMatches(node, search)) continue;
                _contentPanel.Children.Add(CreateCategoryExpander(node, search, 0));
            }

            if (_contentPanel.Children.Count == 0)
            {
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(search)
                        ? "표시할 Property가 없습니다."
                        : "검색 결과가 없습니다.",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(6, 16, 6, 6)
                });
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private CategoryNode BuildCategoryTree(IEnumerable<PropertySchema> properties)
    {
        var root = new CategoryNode { Name = "Root", FullPath = string.Empty, Order = 0 };
        var order = 0;

        foreach (var property in properties.OrderBy(x => x.Order))
        {
            var category = string.IsNullOrWhiteSpace(property.Category) ? "General" : property.Category;
            var segments = category.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) segments = ["General"];

            var current = root;
            var pathParts = new List<string>();
            foreach (var segment in segments)
            {
                pathParts.Add(segment);
                var fullPath = string.Join('|', pathParts);
                var child = current.Children.FirstOrDefault(x => x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new CategoryNode { Name = segment, FullPath = fullPath, Order = order++ };
                    current.Children.Add(child);
                }
                current = child;
            }
            current.Properties.Add(property);
            current.Order = Math.Min(current.Order, property.Order);
        }

        return root;
    }

    private FrameworkElement CreateCategoryExpander(CategoryNode node, string search, int depth)
    {
        var panel = new StackPanel();

        foreach (var property in node.Properties.OrderBy(x => x.Order))
        {
            if (!PropertyMatches(property, search)) continue;
            panel.Children.Add(CreatePropertyRow(_root, property, property.Name));
        }

        foreach (var child in node.Children.OrderBy(x => x.Order))
        {
            if (!CategoryMatches(child, search)) continue;
            panel.Children.Add(CreateCategoryExpander(child, search, depth + 1));
        }

        var expander = new Expander
        {
            Header = node.Name,
            Tag = node.FullPath,
            Content = panel,
            Margin = new Thickness(depth == 0 ? 0 : 8, 2, 0, 2),
            Padding = new Thickness(2),
            FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal
        };

        var searching = !string.IsNullOrWhiteSpace(search);
        var collapsed = GetCollapsedCategories();
        expander.IsExpanded = searching || !collapsed.Contains(node.FullPath);

        if (!searching)
        {
            expander.Expanded += (_, _) => PersistCategoryState(node.FullPath, collapsed: false);
            expander.Collapsed += (_, _) => PersistCategoryState(node.FullPath, collapsed: true);
        }

        _categoryExpanders.Add(expander);
        return expander;
    }

    private FrameworkElement CreatePropertyRow(JsonObject owner, PropertySchema schema, string path)
    {
        if (!owner.ContainsKey(schema.Name))
            owner[schema.Name] = SchemaValueService.CreateDefault(schema);

        if (schema.Type == "struct")
            return CreateStructEditor(owner, schema, path);
        if (schema.Type is "array" or "set")
            return CreateArrayEditor(owner, schema, path);
        if (schema.Type == "map")
            return CreateMapEditor(owner, schema, path);
        if (schema.Type == "gameplayTagContainer")
            return CreateTagContainerEditor(owner, schema, path);

        var grid = new Grid { Margin = new Thickness(8, 4, 4, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = schema.EffectiveDisplayName,
            ToolTip = string.IsNullOrWhiteSpace(schema.Tooltip) ? null : schema.Tooltip,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var editor = CreateScalarEditor(
            schema,
            owner[schema.Name],
            newValue => owner[schema.Name] = newValue?.DeepClone(),
            path);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private FrameworkElement CreateStructEditor(JsonObject owner, PropertySchema schema, string path)
    {
        var structObject = owner[schema.Name] as JsonObject ?? new JsonObject();
        owner[schema.Name] = structObject;
        SchemaValueService.EnsureSchemaDefaults(structObject, schema.Properties);

        var panel = new StackPanel();
        foreach (var child in schema.Properties.OrderBy(x => x.Order))
            panel.Children.Add(CreatePropertyRow(structObject, child, $"{path}.{child.Name}"));

        return new Expander
        {
            Header = schema.EffectiveDisplayName,
            IsExpanded = true,
            Content = panel,
            Margin = new Thickness(8, 4, 4, 4),
            ToolTip = string.IsNullOrWhiteSpace(schema.Tooltip) ? null : schema.Tooltip
        };
    }

    private FrameworkElement CreateArrayEditor(JsonObject owner, PropertySchema schema, string path)
    {
        var array = owner[schema.Name] as JsonArray ?? new JsonArray();
        owner[schema.Name] = array;
        return CreateArrayValueEditor(
            schema,
            array,
            value => owner[schema.Name] = value?.DeepClone(),
            path,
            schema.EffectiveDisplayName);
    }

    private FrameworkElement CreateArrayValueEditor(
        PropertySchema schema,
        JsonArray array,
        Action<JsonNode?> setter,
        string path,
        string title)
    {
        var outer = new StackPanel();

        var header = new DockPanel();
        var add = new Button { Content = "+ 추가", HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) => CommitMutation(() =>
        {
            array.Add(SchemaValueService.CreateDefault(schema.Element ?? new PropertySchema { Type = "string" }));
            setter(array);
        }, rebuild: true);
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrWhiteSpace(schema.Tooltip) ? null : schema.Tooltip
        });
        outer.Children.Add(header);

        for (var i = 0; i < array.Count; i++)
        {
            var index = i;
            var row = new DockPanel { Margin = new Thickness(10, 3, 2, 3) };
            var remove = new Button { Content = "삭제", Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) => CommitMutation(() =>
            {
                array.RemoveAt(index);
                setter(array);
            }, rebuild: true);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = $"[{index}]",
                Width = 45,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (schema.Element is not null)
            {
                var editor = CreateValueEditor(
                    schema.Element,
                    array[index],
                    value =>
                    {
                        array[index] = value?.DeepClone();
                        setter(array);
                    },
                    $"{path}[{index}]");
                row.Children.Add(editor);
            }
            outer.Children.Add(row);
        }

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(8, 5, 4, 5),
            Child = outer
        };
    }

    private FrameworkElement CreateMapEditor(JsonObject owner, PropertySchema schema, string path)
    {
        var map = owner[schema.Name] as JsonObject ?? new JsonObject();
        owner[schema.Name] = map;
        return CreateMapValueEditor(
            schema,
            map,
            value => owner[schema.Name] = value?.DeepClone(),
            path,
            schema.EffectiveDisplayName);
    }

    private FrameworkElement CreateMapValueEditor(
        PropertySchema schema,
        JsonObject map,
        Action<JsonNode?> setter,
        string path,
        string title)
    {
        var outer = new StackPanel();

        var header = new DockPanel();
        var add = new Button { Content = "+ Entry" };
        add.Click += (_, _) => CommitMutation(() =>
        {
            var key = MakeUniqueMapKey(map);
            map[key] = SchemaValueService.CreateDefault(schema.Value ?? new PropertySchema { Type = "string" });
            setter(map);
        }, rebuild: true);
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        outer.Children.Add(header);

        foreach (var pair in map.ToList())
        {
            var originalKey = pair.Key;
            var row = new DockPanel { Margin = new Thickness(10, 3, 2, 3) };
            var remove = new Button { Content = "삭제", Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) => CommitMutation(() =>
            {
                map.Remove(originalKey);
                setter(map);
            }, rebuild: true);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var keyBox = new TextBox { Text = originalKey, Width = 150, Margin = new Thickness(0, 0, 8, 0) };
            keyBox.LostFocus += (_, _) =>
            {
                var newKey = keyBox.Text.Trim();
                if (newKey == originalKey) return;
                if (string.IsNullOrWhiteSpace(newKey) || map.ContainsKey(newKey))
                {
                    keyBox.BorderBrush = Brushes.Firebrick;
                    keyBox.ToolTip = "Map Key는 비어 있거나 중복될 수 없습니다.";
                    return;
                }

                CommitMutation(() =>
                {
                    var node = map[originalKey];
                    map.Remove(originalKey);
                    map[newKey] = node;
                    setter(map);
                }, rebuild: true);
            };
            row.Children.Add(keyBox);

            if (schema.Value is not null)
            {
                var valueEditor = CreateValueEditor(
                    schema.Value,
                    pair.Value,
                    value =>
                    {
                        map[originalKey] = value?.DeepClone();
                        setter(map);
                    },
                    $"{path}[{originalKey}]");
                row.Children.Add(valueEditor);
            }
            outer.Children.Add(row);
        }

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(8, 5, 4, 5),
            Child = outer
        };
    }

    private FrameworkElement CreateTagContainerEditor(JsonObject owner, PropertySchema schema, string path)
    {
        var tags = owner[schema.Name] as JsonArray ?? new JsonArray();
        owner[schema.Name] = tags;
        // TagContainer는 문자열 목록으로 편집한다.
        var outer = new StackPanel();
        var header = new DockPanel();
        var add = new Button { Content = "+ Tag" };
        add.Click += (_, _) => CommitMutation(() => tags.Add(string.Empty), rebuild: true);
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(new TextBlock { Text = schema.EffectiveDisplayName, FontWeight = FontWeights.SemiBold });
        outer.Children.Add(header);

        for (var i = 0; i < tags.Count; i++)
        {
            var index = i;
            var row = new DockPanel { Margin = new Thickness(10, 3, 2, 3) };
            var remove = new Button { Content = "삭제", Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) => CommitMutation(() => tags.RemoveAt(index), rebuild: true);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var box = new TextBox { Text = SchemaValueService.NodeToEditorText(tags[index]) };
            box.LostFocus += (_, _) =>
            {
                var old = SchemaValueService.NodeToEditorText(tags[index]);
                if (old == box.Text) return;
                CommitMutation(() => tags[index] = box.Text);
            };
            row.Children.Add(box);
            outer.Children.Add(row);
        }

        return new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(8, 5, 4, 5),
            Child = outer
        };
    }

    private FrameworkElement CreateValueEditor(
        PropertySchema schema,
        JsonNode? node,
        Action<JsonNode?> setter,
        string path)
    {
        if (schema.Type == "struct")
        {
            var obj = node as JsonObject ?? new JsonObject();
            SchemaValueService.EnsureSchemaDefaults(obj, schema.Properties);
            var panel = new StackPanel();
            foreach (var child in schema.Properties.OrderBy(x => x.Order))
            {
                if (!obj.ContainsKey(child.Name)) obj[child.Name] = SchemaValueService.CreateDefault(child);
                panel.Children.Add(CreateNestedProperty(obj, child, $"{path}.{child.Name}"));
            }
            return new Expander { Header = schema.EffectiveDisplayName, Content = panel, IsExpanded = true };
        }

        if (schema.Type is "array" or "set")
        {
            var array = node as JsonArray ?? new JsonArray();
            if (node is not JsonArray) setter(array);
            return CreateArrayValueEditor(schema, array, setter, path, schema.EffectiveDisplayName);
        }

        if (schema.Type == "map")
        {
            var map = node as JsonObject ?? new JsonObject();
            if (node is not JsonObject) setter(map);
            return CreateMapValueEditor(schema, map, setter, path, schema.EffectiveDisplayName);
        }

        if (schema.Type == "gameplayTagContainer")
        {
            var tags = node as JsonArray ?? new JsonArray();
            var pseudoSchema = new PropertySchema
            {
                Name = schema.Name,
                DisplayName = schema.DisplayName,
                Type = "array",
                Element = new PropertySchema { Name = "Tag", DisplayName = "Tag", Type = "gameplayTag" }
            };
            if (node is not JsonArray) setter(tags);
            return CreateArrayValueEditor(pseudoSchema, tags, setter, path, schema.EffectiveDisplayName);
        }

        return CreateScalarEditor(schema, node, setter, path);
    }

    private FrameworkElement CreateNestedProperty(JsonObject owner, PropertySchema schema, string path)
    {
        // 중첩 Struct 안에서도 동일한 재귀 편집기를 사용한다.
        if (schema.Type == "struct")
        {
            var obj = owner[schema.Name] as JsonObject ?? new JsonObject();
            owner[schema.Name] = obj;
            SchemaValueService.EnsureSchemaDefaults(obj, schema.Properties);
            var panel = new StackPanel();
            foreach (var child in schema.Properties.OrderBy(x => x.Order))
                panel.Children.Add(CreateNestedProperty(obj, child, $"{path}.{child.Name}"));
            return new Expander { Header = schema.EffectiveDisplayName, Content = panel, IsExpanded = true, Margin = new Thickness(4) };
        }

        var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = schema.EffectiveDisplayName, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(label);
        var editor = CreateValueEditor(schema, owner[schema.Name], value => owner[schema.Name] = value?.DeepClone(), path);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private FrameworkElement CreateScalarEditor(
        PropertySchema schema,
        JsonNode? node,
        Action<JsonNode?> setter,
        string path)
    {
        if (schema.Type == "bool")
        {
            var value = false;
            if (node is JsonValue jv) jv.TryGetValue(out value);
            var check = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            check.Checked += (_, _) => { if (!_rebuilding) CommitMutation(() => setter(JsonValue.Create(true))); };
            check.Unchecked += (_, _) => { if (!_rebuilding) CommitMutation(() => setter(JsonValue.Create(false))); };
            return check;
        }

        if (schema.Type == "enum")
        {
            var combo = new ComboBox
            {
                ItemsSource = schema.EnumValues,
                DisplayMemberPath = nameof(EnumOption.DisplayName),
                SelectedValuePath = nameof(EnumOption.Name),
                SelectedValue = SchemaValueService.NodeToEditorText(node, schema),
                MinWidth = 180
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (_rebuilding || combo.SelectedValue is null) return;
                var newValue = Convert.ToString(combo.SelectedValue) ?? string.Empty;
                if (newValue == SchemaValueService.NodeToEditorText(node, schema)) return;
                CommitMutation(() => setter(JsonValue.Create(newValue)));
            };
            return combo;
        }

        var box = new TextBox
        {
            Text = SchemaValueService.NodeToEditorText(node, schema),
            MinWidth = 160,
            ToolTip = BuildToolTip(schema)
        };

        box.LostFocus += (_, _) =>
        {
            var original = SchemaValueService.NodeToEditorText(node, schema);
            if (box.Text == original) return;

            var parsed = SchemaValueService.ParseEditorText(box.Text, schema, out var error);
            if (error is not null)
            {
                box.BorderBrush = Brushes.Firebrick;
                box.ToolTip = error;
                return;
            }

            var issues = new List<ValidationIssue>();
            SchemaValueService.ValidateNode(parsed, schema, path, issues);
            if (issues.Count > 0)
            {
                box.BorderBrush = Brushes.Firebrick;
                box.ToolTip = issues[0].Message;
                return;
            }

            box.ClearValue(Control.BorderBrushProperty);
            box.ToolTip = BuildToolTip(schema);
            CommitMutation(() => setter(parsed));
        };

        return box;
    }

    private string? BuildToolTip(PropertySchema schema)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(schema.Tooltip)) parts.Add(schema.Tooltip);
        if (schema.Constraints is not null)
        {
            if (!string.IsNullOrWhiteSpace(schema.Constraints.ClampMin)) parts.Add($"Min: {schema.Constraints.ClampMin}");
            if (!string.IsNullOrWhiteSpace(schema.Constraints.ClampMax)) parts.Add($"Max: {schema.Constraints.ClampMax}");
            if (!string.IsNullOrWhiteSpace(schema.Constraints.Units)) parts.Add($"Units: {schema.Constraints.Units}");
        }
        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    private void CommitMutation(Action mutation, bool rebuild = false)
    {
        if (_rebuilding) return;
        _history.PushBeforeChange(SerializeDocument());
        mutation();
        if (rebuild) RebuildContent();
        UpdateDirtyFromSnapshot();
    }

    private HashSet<string> GetCollapsedCategories()
    {
        if (!_settings.CollapsedCategories.TryGetValue(Entry.Id, out var list))
        {
            list = [];
            _settings.CollapsedCategories[Entry.Id] = list;
        }
        return list.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void PersistCategoryState(string path, bool collapsed)
    {
        if (_rebuilding || !string.IsNullOrWhiteSpace(_searchBox.Text)) return;
        var set = GetCollapsedCategories();
        if (collapsed) set.Add(path); else set.Remove(path);
        _settings.CollapsedCategories[Entry.Id] = set.OrderBy(x => x).ToList();
        _settingsStore.Save(_settings);
    }

    private static bool PropertyMatches(PropertySchema schema, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (schema.Name.Contains(search, comparison) ||
            schema.EffectiveDisplayName.Contains(search, comparison) ||
            schema.Category.Contains(search, comparison) ||
            schema.Tooltip.Contains(search, comparison))
            return true;

        return schema.Properties.Any(x => PropertyMatches(x, search)) ||
               (schema.Element is not null && PropertyMatches(schema.Element, search)) ||
               (schema.Value is not null && PropertyMatches(schema.Value, search));
    }

    private static bool CategoryMatches(CategoryNode node, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        node.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        node.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        node.Properties.Any(x => PropertyMatches(x, search)) ||
        node.Children.Any(x => CategoryMatches(x, search));

    private static string MakeUniqueMapKey(JsonObject map)
    {
        for (var i = 1; ; i++)
        {
            var candidate = $"NewKey_{i}";
            if (!map.ContainsKey(candidate)) return candidate;
        }
    }

    private void SetDirty(bool dirty)
    {
        if (_isDirty == dirty) return;
        _isDirty = dirty;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirtyFromSnapshot() => SetDirty(SerializeDocument() != _savedSnapshot);
}
