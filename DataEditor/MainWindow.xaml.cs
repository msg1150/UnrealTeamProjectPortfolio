using JsonAssetDataEditor.Core;
using JsonAssetDataEditor.Editors;
using System.IO; // Path/File/Directory 및 IO 예외 형식을 사용한다.
using Microsoft.Win32;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JsonAssetDataEditor;

public partial class MainWindow : Window
{
    private readonly UserSettingsStore _settingsStore = new();
    private readonly ManifestService _manifestService = new();
    private readonly UserSettings _settings;
    private readonly ProjectLocator _projectLocator;

    private string? _uprojectPath;
    private string? _projectRoot;
    private ManifestRoot? _manifest;
    private IDataEditor? _currentEditor;
    private TreeViewItem? _currentTreeItem;
    private bool _restoringSelection;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _projectLocator = new ProjectLocator(_settingsStore, _settings);
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += MainWindow_Closing;
        UpdateCommandState();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var project = _projectLocator.ResolveProjectOnStartup();
        if (project is null)
        {
            StatusText.Text = "프로젝트가 선택되지 않았습니다.";
            ProjectText.Text = string.Empty;
            return;
        }

        OpenProject(project);
    }

    private void OpenProject(string uprojectPath)
    {
        try
        {
            if (!ProjectLocator.IsValidProject(uprojectPath))
                throw new FileNotFoundException("선택한 .uproject 파일을 찾을 수 없습니다.", uprojectPath);

            var root = Path.GetDirectoryName(Path.GetFullPath(uprojectPath))
                       ?? throw new InvalidOperationException("프로젝트 루트를 확인할 수 없습니다.");

            var manifest = _manifestService.Load(root);

            _uprojectPath = Path.GetFullPath(uprojectPath);
            _projectRoot = root;
            _manifest = manifest;
            _projectLocator.RememberProject(_uprojectPath);

            CloseCurrentEditor();
            PopulateTree();

            ProjectText.Text = $"{manifest.ProjectName} · {_uprojectPath}";
            StatusText.Text = $"Manifest 로드 완료 · DataTable {manifest.DataTables.Count}개 / CurveTable {manifest.CurveTables.Count}개 / FloatCurve {manifest.FloatCurves.Count}개 / DataAsset {manifest.DataAssets.Count}개";
            Title = $"JSON Asset Data Editor - {manifest.ProjectName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "프로젝트 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "프로젝트를 열지 못했습니다.";
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private void PopulateTree()
    {
        AssetTree.Items.Clear();
        if (_manifest is null) return;

        var dataTables = new TreeViewItem { Header = $"DataTables ({_manifest.DataTables.Count})", IsExpanded = true };
        foreach (var entry in _manifest.DataTables.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            dataTables.Items.Add(CreateEntryTreeItem(entry));

        var curveTables = new TreeViewItem { Header = $"CurveTables ({_manifest.CurveTables.Count})", IsExpanded = true };
        foreach (var entry in _manifest.CurveTables.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            curveTables.Items.Add(CreateEntryTreeItem(entry));

        var floatCurves = new TreeViewItem { Header = $"FloatCurves ({_manifest.FloatCurves.Count})", IsExpanded = true };
        foreach (var entry in _manifest.FloatCurves.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            floatCurves.Items.Add(CreateEntryTreeItem(entry));

        var dataAssets = new TreeViewItem { Header = $"DataAssets ({_manifest.DataAssets.Count})", IsExpanded = true };
        foreach (var entry in _manifest.DataAssets.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            dataAssets.Items.Add(CreateEntryTreeItem(entry));

        AssetTree.Items.Add(dataTables);
        AssetTree.Items.Add(curveTables);
        AssetTree.Items.Add(floatCurves);
        AssetTree.Items.Add(dataAssets);
    }

    private static TreeViewItem CreateEntryTreeItem(ManifestEntry entry) => new()
    {
        Header = entry.DisplayName,
        Tag = entry,
        ToolTip = $"{entry.Source}\n→ {entry.Target}"
    };

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_restoringSelection || e.NewValue is not TreeViewItem item || item.Tag is not ManifestEntry entry)
            return;

        if (!ConfirmLeaveCurrentDocument())
        {
            RestoreTreeSelection();
            return;
        }

        OpenEntry(entry, item);
    }

    private void OpenEntry(ManifestEntry entry, TreeViewItem treeItem)
    {
        if (_projectRoot is null) return;

        try
        {
            var sourcePath = ResolveSourcePath(entry);
            string jsonText;

            if (!File.Exists(sourcePath))
            {
                var answer = MessageBox.Show(
                    this,
                    $"Source JSON 파일이 없습니다.\n\n{sourcePath}\n\nSchema 기본값으로 새 파일을 만들까요?",
                    "JSON 파일 없음",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                    return;

                jsonText = CreateDefaultDocument(entry);
                AtomicFile.WriteAllText(sourcePath, jsonText);
            }
            else
            {
                jsonText = File.ReadAllText(sourcePath);
            }

            CloseCurrentEditor();
            _currentEditor = entry.IsDataTable
                ? new DataTableEditorControl(entry, jsonText)
                : entry.IsCurveTable
                    ? new CurveTableEditorControl(entry, jsonText)
                    : entry.IsFloatCurve
                        ? new FloatCurveEditorControl(entry, jsonText)
                        : new DataAssetEditorControl(entry, jsonText, _settingsStore, _settings);

            _currentEditor.DirtyStateChanged += CurrentEditor_DirtyStateChanged;
            EditorHost.Content = _currentEditor;
            EmptyText.Visibility = Visibility.Collapsed;
            _currentTreeItem = treeItem;

            StatusText.Text = $"{entry.AssetType} · {entry.Source}";
            UpdateWindowTitle();
            UpdateCommandState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "데이터 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string ResolveSourcePath(ManifestEntry entry)
    {
        if (_projectRoot is null) throw new InvalidOperationException("프로젝트가 열려 있지 않습니다.");

        var root = Path.GetFullPath(_projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var relative = entry.Source.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        // 잘못된 Manifest가 프로젝트 바깥 파일을 수정하지 못하게 막는다.
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source 경로가 프로젝트 루트 밖을 가리킵니다: {entry.Source}");

        return full;
    }

    private static string CreateDefaultDocument(ManifestEntry entry)
    {
        if (entry.IsDataTable || entry.IsCurveTable)
            return new JsonArray().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (entry.IsFloatCurve)
        {
            var floatCurve = new JsonObject
            {
                ["defaultValue"] = 0.0f,
                ["preInfinityExtrap"] = "RCCE_Constant",
                ["postInfinityExtrap"] = "RCCE_Constant",
                ["keys"] = new JsonArray()
            };

            return floatCurve.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }
            );
        }

        var obj = new JsonObject();
        foreach (var property in entry.Properties.OrderBy(x => x.Order))
            obj[property.Name] = SchemaValueService.CreateDefault(property);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private bool SaveCurrent()
    {
        if (_currentEditor is null) return true;

        try
        {
            var issues = _currentEditor.ValidateDocument();
            if (issues.Count > 0)
            {
                var visible = string.Join(Environment.NewLine, issues.Take(20).Select(x => "• " + x));
                if (issues.Count > 20) visible += $"\n... 외 {issues.Count - 20}개";
                MessageBox.Show(
                    this,
                    "Schema 검증에 실패하여 저장하지 않았습니다.\n\n" + visible,
                    "저장 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                StatusText.Text = $"검증 실패 · 오류 {issues.Count}개";
                return false;
            }

            var path = ResolveSourcePath(_currentEditor.Entry);
            AtomicFile.WriteAllText(path, _currentEditor.SerializeDocument(indented: true));
            _currentEditor.MarkSaved();
            StatusText.Text = $"저장 완료 · {_currentEditor.Entry.Source}";
            UpdateWindowTitle();
            UpdateCommandState();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "저장 실패";
            return false;
        }
    }

    private bool ConfirmLeaveCurrentDocument()
    {
        if (_currentEditor is null || !_currentEditor.IsDirty) return true;

        var answer = MessageBox.Show(
            this,
            $"{_currentEditor.Entry.DisplayName}에 저장하지 않은 변경사항이 있습니다.\n\n저장하고 계속할까요?",
            "저장하지 않은 변경사항",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return answer switch
        {
            MessageBoxResult.Yes => SaveCurrent(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void RestoreTreeSelection()
    {
        if (_currentTreeItem is null) return;
        _restoringSelection = true;
        Dispatcher.BeginInvoke(() =>
        {
            _currentTreeItem.IsSelected = true;
            _currentTreeItem.BringIntoView();
            _restoringSelection = false;
        }, DispatcherPriority.Background);
    }

    private void CloseCurrentEditor()
    {
        if (_currentEditor is not null)
            _currentEditor.DirtyStateChanged -= CurrentEditor_DirtyStateChanged;
        _currentEditor = null;
        _currentTreeItem = null;
        EditorHost.Content = null;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void CurrentEditor_DirtyStateChanged(object? sender, EventArgs e)
    {
        UpdateWindowTitle();
        UpdateCommandState();
    }

    private void UpdateWindowTitle()
    {
        var project = _manifest?.ProjectName ?? "No Project";
        var document = _currentEditor is null ? string.Empty : $" - {_currentEditor.Entry.DisplayName}";
        var dirty = _currentEditor?.IsDirty == true ? " *" : string.Empty;
        Title = $"JSON Asset Data Editor - {project}{document}{dirty}";
    }

    private void UpdateCommandState()
    {
        ImportCsvMenu.IsEnabled = _currentEditor?.SupportsCsv == true;
        ExportCsvMenu.IsEnabled = _currentEditor?.SupportsCsv == true;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveCurrentDocument()) return;
        var project = _projectLocator.PromptForProject();
        if (project is not null) OpenProject(project);
    }

    private void ReloadManifest_Click(object sender, RoutedEventArgs e)
    {
        if (_uprojectPath is null) return;
        if (!ConfirmLeaveCurrentDocument()) return;
        OpenProject(_uprojectPath);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditor?.SupportsCsv != true) return;
        var dialog = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _currentEditor.ImportCsv(File.ReadAllText(dialog.FileName));
            StatusText.Text = $"CSV 가져오기 완료 · {Path.GetFileName(dialog.FileName)}";
            UpdateWindowTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CSV 가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditor?.SupportsCsv != true) return;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = _currentEditor.Entry.DisplayName + ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _currentEditor.ExportCsv(), new System.Text.UTF8Encoding(true));
            StatusText.Text = $"CSV 내보내기 완료 · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CSV 내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _currentEditor?.Undo();
        UpdateWindowTitle();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _currentEditor?.Redo();
        UpdateWindowTitle();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => _currentEditor?.ExpandAll();
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => _currentEditor?.CollapseAll();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
        {
            SaveCurrent(); e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.O)
        {
            OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
        {
            _currentEditor?.Undo(); e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
        {
            _currentEditor?.Redo(); e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ReloadManifest_Click(this, new RoutedEventArgs()); e.Handled = true;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmLeaveCurrentDocument()) e.Cancel = true;
    }
}
