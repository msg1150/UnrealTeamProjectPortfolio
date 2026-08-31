using System.Text.Json.Serialization;

namespace JsonAssetDataEditor.Core;

public sealed class ManifestRoot
{
    public int ManifestVersion { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Registry { get; set; } = string.Empty;
    public List<ManifestEntry> DataTables { get; set; } = [];
    public List<ManifestEntry> CurveTables { get; set; } = [];
    public List<ManifestEntry> FloatCurves { get; set; } = [];
    public List<ManifestEntry> DataAssets { get; set; } = [];
}

public sealed class ManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? RowStruct { get; set; }
    public string? CurveTableMode { get; set; }
    public int? InterpMode { get; set; }

    [JsonPropertyName("class")]
    public string? ClassPath { get; set; }

    public List<PropertySchema> Properties { get; set; } = [];

    public bool IsDataTable => AssetType.Equals("DataTable", StringComparison.OrdinalIgnoreCase);
    public bool IsCurveTable => AssetType.Equals("CurveTable", StringComparison.OrdinalIgnoreCase);
    public bool IsFloatCurve => AssetType.Equals("FloatCurve", StringComparison.OrdinalIgnoreCase);
    public bool IsDataAsset => AssetType.Equals("DataAsset", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}

public sealed class PropertySchema
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "unsupported";
    public string CppType { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Category { get; set; } = "General";
    public string Tooltip { get; set; } = string.Empty;
    public bool AdvancedDisplay { get; set; }

    public string? StructType { get; set; }
    public string? StructPath { get; set; }
    public string? ObjectClass { get; set; }
    public string? MetaClass { get; set; }

    public PropertyConstraints? Constraints { get; set; }
    public List<EnumOption> EnumValues { get; set; } = [];
    public List<PropertySchema> Properties { get; set; } = [];
    public PropertySchema? Element { get; set; }
    public PropertySchema? Key { get; set; }
    public PropertySchema? Value { get; set; }

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public bool IsContainer => Type is "array" or "set" or "map";
    public bool IsStruct => Type == "struct";
    public bool IsComplex => IsContainer || IsStruct || Type == "gameplayTagContainer";
}

public sealed class PropertyConstraints
{
    public string? ClampMin { get; set; }
    public string? ClampMax { get; set; }
    public string? UiMin { get; set; }
    public string? UiMax { get; set; }
    public string? Multiple { get; set; }
    public string? Units { get; set; }
}

public sealed class EnumOption
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public sealed class UserSettings
{
    public string LastProject { get; set; } = string.Empty;
    public List<string> RecentProjects { get; set; } = [];

    // Entry Id -> 접힌 Category 경로 목록
    public Dictionary<string, List<string>> CollapsedCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ValidationIssue(string Path, string Message)
{
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

public interface IDataEditor
{
    ManifestEntry Entry { get; }
    bool IsDirty { get; }
    event EventHandler? DirtyStateChanged;

    IReadOnlyList<ValidationIssue> ValidateDocument();
    string SerializeDocument(bool indented = true);
    void LoadSerializedDocument(string json, bool resetHistory);
    void MarkSaved();
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();

    bool SupportsCsv { get; }
    void ImportCsv(string csvText);
    string ExportCsv();

    void ExpandAll();
    void CollapseAll();
}
