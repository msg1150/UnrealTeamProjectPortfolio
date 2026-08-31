using System.IO; // Path/File/Directory 및 IO 예외 형식을 사용한다.
using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonAssetDataEditor.Core;

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JsonAssetDataEditor");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new UserSettings();

            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
    }
}

public sealed class ProjectLocator
{
    private readonly UserSettingsStore _settingsStore;
    private readonly UserSettings _settings;

    public ProjectLocator(UserSettingsStore settingsStore, UserSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
    }

    public string? ResolveProjectOnStartup()
    {
        // .uproject를 EXE에 드래그하거나 명령줄 인자로 넘긴 경우를 최우선으로 사용한다.
        var commandLineProject = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(IsValidProject);
        if (commandLineProject is not null)
        {
            RememberProject(commandLineProject);
            return Path.GetFullPath(commandLineProject);
        }

        if (IsValidProject(_settings.LastProject))
            return Path.GetFullPath(_settings.LastProject);

        // EXE를 프로젝트 안에 두고 실행하는 경우를 위한 보조 탐색이다.
        var fromExe = FindProjectNearExecutable();
        if (fromExe is not null)
        {
            RememberProject(fromExe);
            return fromExe;
        }

        return PromptForProject();
    }

    public string? PromptForProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Unreal 프로젝트를 선택하세요",
            Filter = "Unreal Project (*.uproject)|*.uproject",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return null;

        RememberProject(dialog.FileName);
        return Path.GetFullPath(dialog.FileName);
    }

    public void RememberProject(string uprojectPath)
    {
        var full = Path.GetFullPath(uprojectPath);
        _settings.LastProject = full;
        _settings.RecentProjects.RemoveAll(x => x.Equals(full, StringComparison.OrdinalIgnoreCase));
        _settings.RecentProjects.Insert(0, full);
        if (_settings.RecentProjects.Count > 5)
            _settings.RecentProjects.RemoveRange(5, _settings.RecentProjects.Count - 5);
        _settingsStore.Save(_settings);
    }

    public static bool IsValidProject(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static string? FindProjectNearExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // 무한/광범위 검색을 하지 않고 EXE 상위 6단계까지만 검사한다.
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(directory.FullName, "*.uproject", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            if (projects.Length == 1)
                return projects[0];

            if (projects.Length > 1)
                return null; // 잘못된 프로젝트를 임의 선택하지 않는다.
        }

        return null;
    }
}

public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public const string RelativeManifestPath = "ExternalData/JsonAssetSyncManifest.json";

    public ManifestRoot Load(string projectRoot)
    {
        var path = Path.Combine(projectRoot, RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "외부 데이터 Manifest를 찾을 수 없습니다. Unreal Editor를 한 번 실행하거나 Rebuild External Data Manifest를 실행하세요.",
                path);

        var manifest = JsonSerializer.Deserialize<ManifestRoot>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException("Manifest JSON을 읽지 못했습니다.");

        if (manifest.ManifestVersion != 1)
            throw new InvalidDataException($"지원하지 않는 Manifest 버전입니다: {manifest.ManifestVersion}");

        return manifest;
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("저장 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, text, new UTF8Encoding(false));

        try
        {
            if (File.Exists(path))
            {
                // 같은 볼륨/폴더에서 교체하여 중간 파일 손상 가능성을 줄인다.
                var backup = tempPath + ".bak";
                try
                {
                    File.Replace(tempPath, path, backup, ignoreMetadataErrors: true);
                    if (File.Exists(backup)) File.Delete(backup);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                }

                File.Move(tempPath, path, overwrite: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

public sealed class SnapshotHistory
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private const int MaxHistory = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void PushBeforeChange(string currentSnapshot)
    {
        if (_undo.Count > 0 && _undo.Peek() == currentSnapshot)
            return;

        _undo.Push(currentSnapshot);
        while (_undo.Count > MaxHistory)
        {
            // Stack은 아래 원소 제거 API가 없으므로 오래된 항목을 재구성한다.
            var trimmed = _undo.Reverse().Skip(1).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in trimmed)
                _undo.Push(item);
        }
        _redo.Clear();
    }

    public string? Undo(string currentSnapshot)
    {
        if (_undo.Count == 0) return null;
        _redo.Push(currentSnapshot);
        return _undo.Pop();
    }

    public string? Redo(string currentSnapshot)
    {
        if (_redo.Count == 0) return null;
        _undo.Push(currentSnapshot);
        return _redo.Pop();
    }
}

public static class SchemaValueService
{
    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "byte"
    };

    private static readonly HashSet<string> FloatingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "float", "double"
    };

    public static JsonNode? CreateDefault(PropertySchema schema)
    {
        if (IntegerTypes.Contains(schema.Type)) return JsonValue.Create(0);
        if (schema.Type.Equals("float", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(0.0f);
        if (schema.Type.Equals("double", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(0.0d);

        return schema.Type switch
        {
            "bool" => JsonValue.Create(false),
            "string" or "name" or "text" or "gameplayTag" or "object" or "softObject" or "class" or "softClass" => JsonValue.Create(string.Empty),
            "enum" => JsonValue.Create(schema.EnumValues.FirstOrDefault()?.Name ?? string.Empty),
            "gameplayTagContainer" => new JsonArray(),
            "array" or "set" => new JsonArray(),
            "map" => new JsonObject(),
            "struct" => CreateDefaultStruct(schema),
            _ => null
        };
    }

    private static JsonObject CreateDefaultStruct(PropertySchema schema)
    {
        var result = new JsonObject();
        foreach (var child in schema.Properties.OrderBy(x => x.Order))
            result[child.Name] = CreateDefault(child);
        return result;
    }

    public static void EnsureSchemaDefaults(JsonObject obj, IEnumerable<PropertySchema> properties)
    {
        foreach (var schema in properties.OrderBy(x => x.Order))
        {
            if (!obj.ContainsKey(schema.Name))
                obj[schema.Name] = CreateDefault(schema);

            EnsureNestedDefaults(obj[schema.Name], schema);
        }
    }

    private static void EnsureNestedDefaults(JsonNode? node, PropertySchema schema)
    {
        if (schema.Type == "struct" && node is JsonObject structObject)
            EnsureSchemaDefaults(structObject, schema.Properties);
        else if ((schema.Type == "array" || schema.Type == "set") && node is JsonArray array && schema.Element is not null)
            foreach (var item in array) EnsureNestedDefaults(item, schema.Element);
        else if (schema.Type == "map" && node is JsonObject map && schema.Value is not null)
            foreach (var pair in map) EnsureNestedDefaults(pair.Value, schema.Value);
    }

    public static List<ValidationIssue> ValidateEntry(JsonNode? root, ManifestEntry entry)
    {
        var issues = new List<ValidationIssue>();

        if (entry.IsDataAsset)
        {
            if (root is not JsonObject obj)
            {
                issues.Add(new ValidationIssue(string.Empty, "DataAsset JSON 최상위는 Object여야 합니다."));
                return issues;
            }
            ValidateObject(obj, entry.Properties, string.Empty, issues, strictUnknown: true);
        }
        else if (entry.IsDataTable)
        {
            if (root is not JsonArray array)
            {
                issues.Add(new ValidationIssue(string.Empty, "DataTable JSON 최상위는 Array여야 합니다."));
                return issues;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject row)
                {
                    issues.Add(new ValidationIssue($"[{i}]", "각 Row는 Object여야 합니다."));
                    continue;
                }

                var name = row["Name"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    issues.Add(new ValidationIssue($"[{i}].Name", "Row Name은 비어 있을 수 없습니다."));
                else if (!names.Add(name))
                    issues.Add(new ValidationIssue($"[{i}].Name", "Row Name이 중복되었습니다."));

                ValidateObject(row, entry.Properties, $"[{i}]", issues, strictUnknown: true, allowedExtra: "Name");
            }
        }

        return issues;
    }

    public static void ValidateObject(
        JsonObject obj,
        IEnumerable<PropertySchema> properties,
        string path,
        List<ValidationIssue> issues,
        bool strictUnknown,
        string? allowedExtra = null)
    {
        var schemaMap = properties.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        if (strictUnknown)
        {
            foreach (var pair in obj)
            {
                if (!schemaMap.ContainsKey(pair.Key) && !pair.Key.Equals(allowedExtra, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new ValidationIssue(Join(path, pair.Key), "현재 Unreal Schema에 없는 필드입니다."));
            }
        }

        foreach (var schema in properties)
        {
            if (!TryGetCaseInsensitive(obj, schema.Name, out var node))
            {
                issues.Add(new ValidationIssue(Join(path, schema.Name), "필수 필드가 없습니다."));
                continue;
            }

            ValidateNode(node, schema, Join(path, schema.Name), issues);
        }
    }

    public static void ValidateNode(JsonNode? node, PropertySchema schema, string path, List<ValidationIssue> issues)
    {
        if (schema.Type == "unsupported")
        {
            issues.Add(new ValidationIssue(path, $"외부 편집기가 지원하지 않는 Unreal Property 타입입니다: {schema.CppType}"));
            return;
        }

        if (IntegerTypes.Contains(schema.Type))
        {
            if (!TryInteger(node, out var integerValue))
            {
                issues.Add(new ValidationIssue(path, "정수 값이 필요합니다."));
                return;
            }
            ValidateNumericConstraints(integerValue, schema, path, issues);
            return;
        }

        if (FloatingTypes.Contains(schema.Type))
        {
            if (!TryDouble(node, out var doubleValue))
            {
                issues.Add(new ValidationIssue(path, "숫자 값이 필요합니다."));
                return;
            }
            ValidateNumericConstraints(doubleValue, schema, path, issues);
            return;
        }

        switch (schema.Type)
        {
            case "bool":
                if (node is not JsonValue || !TryBool(node, out _))
                    issues.Add(new ValidationIssue(path, "bool 값이 필요합니다."));
                break;

            case "string":
            case "name":
            case "text":
            case "gameplayTag":
            case "object":
            case "softObject":
            case "class":
            case "softClass":
                if (!TryString(node, out _))
                    issues.Add(new ValidationIssue(path, "문자열 값이 필요합니다."));
                break;

            case "enum":
                if (!TryString(node, out var enumValue) ||
                    !schema.EnumValues.Any(x => x.Name.Equals(enumValue, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new ValidationIssue(path, "Enum에 정의된 값 중 하나를 선택해야 합니다."));
                break;

            case "struct":
                if (node is JsonObject structObject)
                    ValidateObject(structObject, schema.Properties, path, issues, strictUnknown: true);
                else
                    issues.Add(new ValidationIssue(path, "Struct는 JSON Object여야 합니다."));
                break;

            case "array":
            case "set":
                if (node is JsonArray array)
                {
                    if (schema.Element is not null)
                    {
                        for (var i = 0; i < array.Count; i++)
                            ValidateNode(array[i], schema.Element, $"{path}[{i}]", issues);
                    }

                    if (schema.Type == "set")
                    {
                        var serialized = new HashSet<string>(StringComparer.Ordinal);
                        for (var i = 0; i < array.Count; i++)
                        {
                            var key = array[i]?.ToJsonString() ?? "null";
                            if (!serialized.Add(key))
                                issues.Add(new ValidationIssue($"{path}[{i}]", "Set에는 중복된 값을 넣을 수 없습니다."));
                        }
                    }
                }
                else
                    issues.Add(new ValidationIssue(path, "Array/Set은 JSON Array여야 합니다."));
                break;

            case "gameplayTagContainer":
                if (node is not JsonArray tags || tags.Any(x => !TryString(x, out _)))
                    issues.Add(new ValidationIssue(path, "GameplayTagContainer는 문자열 Array여야 합니다."));
                break;

            case "map":
                if (node is JsonObject map)
                {
                    foreach (var pair in map)
                    {
                        if (schema.Key is not null)
                            ValidateMapKey(pair.Key, schema.Key, $"{path}[{pair.Key}]", issues);
                        if (schema.Value is not null)
                            ValidateNode(pair.Value, schema.Value, $"{path}[{pair.Key}]", issues);
                    }
                }
                else
                    issues.Add(new ValidationIssue(path, "Map은 JSON Object여야 합니다."));
                break;
        }
    }

    public static JsonNode? ParseEditorText(string text, PropertySchema schema, out string? error)
    {
        error = null;
        text ??= string.Empty;

        try
        {
            if (IntegerTypes.Contains(schema.Type))
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    error = "정수를 입력하세요.";
                    return null;
                }
                return JsonValue.Create(value);
            }

            if (schema.Type.Equals("float", StringComparison.OrdinalIgnoreCase))
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    error = "float 숫자를 입력하세요.";
                    return null;
                }

                // Unreal float와 동일한 32-bit 정밀도로 저장하여
                // 0.2가 0.20000000298023224처럼 불필요하게 길어지는 것을 막는다.
                return JsonValue.Create(value);
            }

            if (schema.Type.Equals("double", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    error = "double 숫자를 입력하세요.";
                    return null;
                }
                return JsonValue.Create(value);
            }

            return JsonValue.Create(text);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static string NodeToEditorText(JsonNode? node, PropertySchema? schema = null)
    {
        if (node is null) return string.Empty;
        if (TryString(node, out var s)) return s;
        if (TryBool(node, out var b)) return b ? "true" : "false";

        if (TryDouble(node, out var d))
        {
            if (schema?.Type.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
            {
                // float는 Unreal의 32-bit 값으로 한 번 맞춘 뒤
                // Round-trip 가능한 가장 짧은 문자열로 표시한다.
                return ((float)d).ToString("R", CultureInfo.InvariantCulture);
            }

            if (schema?.Type.Equals("double", StringComparison.OrdinalIgnoreCase) == true)
                return d.ToString("R", CultureInfo.InvariantCulture);

            // 타입 정보가 없는 일반 숫자도 사람이 읽기 좋은 짧은 표현을 사용한다.
            return d.ToString("G", CultureInfo.InvariantCulture);
        }

        return node.ToJsonString();
    }

    /**
     * Schema가 float라고 선언한 값은 실제 Unreal float 정밀도로 정규화한다.
     *
     * 기존 JSON에 0.20000000298023224처럼 저장되어 있어도
     * 외부 편집기가 저장할 때 0.2 같은 정상적인 JSON 숫자로 정리된다.
     */
    public static JsonNode? NormalizeNodeToSchema(JsonNode? node, PropertySchema schema)
    {
        if (node is null)
            return CreateDefault(schema);

        if (schema.Type.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            return TryDouble(node, out var d)
                ? JsonValue.Create((float)d)
                : node.DeepClone();
        }

        if (schema.Type.Equals("double", StringComparison.OrdinalIgnoreCase))
        {
            return TryDouble(node, out var d)
                ? JsonValue.Create(d)
                : node.DeepClone();
        }

        if (schema.Type == "struct" && node is JsonObject sourceStruct)
        {
            var result = new JsonObject();
            foreach (var child in schema.Properties.OrderBy(x => x.Order))
            {
                if (TryGetCaseInsensitive(sourceStruct, child.Name, out var childNode))
                    result[child.Name] = NormalizeNodeToSchema(childNode, child);
                else
                    result[child.Name] = CreateDefault(child);
            }

            // Schema에 없는 필드는 Validation에서 잡히도록 원본을 보존한다.
            foreach (var pair in sourceStruct)
            {
                if (!schema.Properties.Any(x => x.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                    result[pair.Key] = pair.Value?.DeepClone();
            }

            return result;
        }

        if ((schema.Type == "array" || schema.Type == "set") &&
            node is JsonArray sourceArray &&
            schema.Element is not null)
        {
            var result = new JsonArray();
            foreach (var item in sourceArray)
                result.Add(NormalizeNodeToSchema(item, schema.Element));
            return result;
        }

        if (schema.Type == "map" &&
            node is JsonObject sourceMap &&
            schema.Value is not null)
        {
            var result = new JsonObject();
            foreach (var pair in sourceMap)
                result[pair.Key] = NormalizeNodeToSchema(pair.Value, schema.Value);
            return result;
        }

        return node.DeepClone();
    }

    public static JsonObject NormalizeDataAssetDocument(JsonObject source, ManifestEntry entry)
    {
        var result = new JsonObject();

        foreach (var property in entry.Properties.OrderBy(x => x.Order))
        {
            if (TryGetCaseInsensitive(source, property.Name, out var node))
                result[property.Name] = NormalizeNodeToSchema(node, property);
            else
                result[property.Name] = CreateDefault(property);
        }

        // Unknown 필드는 Validation에서 사용자에게 알려주기 위해 그대로 보존한다.
        foreach (var pair in source)
        {
            if (!entry.Properties.Any(x => x.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                result[pair.Key] = pair.Value?.DeepClone();
        }

        return result;
    }

    public static bool TryGetCaseInsensitive(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value)) return true;
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string Join(string path, string child) =>
        string.IsNullOrEmpty(path) ? child : $"{path}.{child}";

    private static void ValidateMapKey(string keyText, PropertySchema schema, string path, List<ValidationIssue> issues)
    {
        if (IntegerTypes.Contains(schema.Type) && !long.TryParse(keyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            issues.Add(new ValidationIssue(path, "Map Key가 정수 타입과 맞지 않습니다."));
        else if (FloatingTypes.Contains(schema.Type) && !double.TryParse(keyText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            issues.Add(new ValidationIssue(path, "Map Key가 숫자 타입과 맞지 않습니다."));
        else if (schema.Type == "enum" && !schema.EnumValues.Any(x => x.Name.Equals(keyText, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new ValidationIssue(path, "Map Key가 Enum 값과 맞지 않습니다."));
    }

    private static void ValidateNumericConstraints(double value, PropertySchema schema, string path, List<ValidationIssue> issues)
    {
        if (schema.Constraints is null) return;

        if (double.TryParse(schema.Constraints.ClampMin, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && value < min)
            issues.Add(new ValidationIssue(path, $"최솟값 {min.ToString(CultureInfo.InvariantCulture)}보다 작을 수 없습니다."));
        if (double.TryParse(schema.Constraints.ClampMax, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && value > max)
            issues.Add(new ValidationIssue(path, $"최댓값 {max.ToString(CultureInfo.InvariantCulture)}보다 클 수 없습니다."));
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = string.Empty;
        try
        {
            if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                value = s;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        try { return node is JsonValue jv && jv.TryGetValue<bool>(out value); }
        catch { return false; }
    }

    private static bool TryInteger(JsonNode? node, out long value)
    {
        value = 0;
        try
        {
            if (node is JsonValue jv)
            {
                if (jv.TryGetValue<long>(out value)) return true;
                if (jv.TryGetValue<int>(out var i)) { value = i; return true; }
                if (jv.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) { value = (long)d; return true; }
            }
        }
        catch { }
        return false;
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        try
        {
            if (node is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out value)) return true;
                if (jv.TryGetValue<float>(out var f)) { value = f; return true; }
                if (jv.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
                if (jv.TryGetValue<long>(out var l)) { value = l; return true; }
                if (jv.TryGetValue<int>(out var i)) { value = i; return true; }
            }
        }
        catch { }
        return false;
    }
}

public static class CsvCodec
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"') quoted = true;
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n')
                {
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                }
                else field.Append(c);
            }
        }

        if (quoted)
            throw new InvalidDataException("CSV의 따옴표가 닫히지 않았습니다.");

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    public static string Write(IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(Escape)));
        }
        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('"')) value = value.Replace("\"", "\"\"");
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value}\"" : value;
    }
}
