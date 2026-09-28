using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

/// <summary>服务商列表行。</summary>
public partial class ModelProviderRowViewModel : ViewModelBase
{
    public required ProviderInfo Provider { get; set; }

    public string Name => Provider.Name;
    public string DisplayName => string.IsNullOrEmpty(Provider.DisplayName) ? Provider.Name : Provider.DisplayName!;
    public bool HasAltId => !string.IsNullOrEmpty(Provider.DisplayName) && !string.Equals(Provider.DisplayName, Provider.Name, StringComparison.Ordinal);
    public string ApiText => string.IsNullOrEmpty(Provider.Api) ? "(默认)" : Provider.Api!;
    public string BaseUrlText => string.IsNullOrEmpty(Provider.BaseUrl) ? "—" : Provider.BaseUrl!;

    /// <summary>Key 脱敏展示:环境变量/命令形式原样提示,明文只露前 8 位。</summary>
    public string KeyText
    {
        get
        {
            var k = Provider.ApiKey?.Trim();
            if (string.IsNullOrEmpty(k)) return "(未设置)";
            if (k.StartsWith('$')) return "环境变量 " + k;
            if (k.StartsWith('!')) return "命令 " + (k.Length > 20 ? k[..20] + "…" : k);
            return k.Length <= 8 ? k : k[..8] + "…(已隐藏)";
        }
    }

    public int ModelCount => Provider.Models.Count;
}

/// <summary>模型列表行。</summary>
public partial class CustomModelRowViewModel : ViewModelBase
{
    public required CustomModelDef Model { get; set; }

    public string Id => Model.Id;
    public string Name => string.IsNullOrEmpty(Model.Name) ? Model.Id : Model.Name!;
    public bool HasAltName => !string.IsNullOrEmpty(Model.Name) && !string.Equals(Model.Name, Model.Id, StringComparison.Ordinal);
    public string ReasoningText => Model.Reasoning ? "✓ 推理" : "—";
    public string InputText => Model.Input.Count == 0 ? "text(默认)" : string.Join(" + ", Model.Input);
    public string ContextText => Model.ContextWindow is > 0 ? Model.ContextWindow.Value.ToString("N0") : "—";
    public string MaxTokensText => Model.MaxTokens is > 0 ? Model.MaxTokens.Value.ToString("N0") : "—";
    public string ApiText => string.IsNullOrEmpty(Model.Api) ? "—" : Model.Api!;
    public bool HasApiOverride => !string.IsNullOrEmpty(Model.Api);

    /// <summary>「上下文 / 最大输出」合并展示。</summary>
    public string LimitsText => $"{ContextText} / {MaxTokensText}";

    public string CostText
    {
        get
        {
            var i = Model.CostInput;
            var o = Model.CostOutput;
            var r = Model.CostCacheRead;
            var w = Model.CostCacheWrite;
            if (i is null && o is null && r is null && w is null)
                return "—";
            static string F(double? v) => (v ?? 0).ToString("0.######");
            return $"入{F(i)} / 出{F(o)} / 读{F(r)} / 写{F(w)}";
        }
    }

    public string ParamsText
    {
        get
        {
            var sp = Model.SamplingParams;
            if (sp == null || sp.Count == 0) return "—";
            return string.Join(", ", sp.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString() ?? "null"}"));
        }
    }
}

/// <summary>
/// 「自定义模型」页:手动编辑 ~/.pi/agent/models.json。
/// 上半区为服务商(URL + Key)列表,下半区为选中服务商下的模型列表;
/// 采样参数(temperature 等)以输入方式拼接进模型条目。
/// </summary>
public partial class ModelsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly ModelSettingsService _service;

    public MainViewModel? Main { get; set; }

    public ObservableCollection<ModelProviderRowViewModel> Providers { get; } = new();

    [ObservableProperty]
    private ModelProviderRowViewModel? _selectedProvider;

    public ObservableCollection<CustomModelRowViewModel> Models { get; } = new();

    [ObservableProperty]
    private CustomModelRowViewModel? _selectedModel;

    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private string _fileHint = "";

    [ObservableProperty]
    private string _providerSummary = "";

    public bool HasSelectedProvider => SelectedProvider != null;

    public bool HasProviders => Providers.Count > 0;

    public bool HasModels => Models.Count > 0;

    public string ModelsFile => _service.ModelsFile;

    public ModelsViewModel(PiEnvironment env, ModelSettingsService service)
    {
        _env = env;
        _service = service;
    }

    private string? _lastLoadError;

    public void Refresh()
    {
        var prev = SelectedProvider?.Name;
        Providers.Clear();
        _lastLoadError = null;
        try
        {
            foreach (var p in _service.ListProviders())
                Providers.Add(new ModelProviderRowViewModel { Provider = p });
        }
        catch (Exception ex)
        {
            // models.json 损坏时列表为空;记录原因给 FileHint,可通过「编辑原始 JSON」修复
            _lastLoadError = ex.Message;
        }
        HasFile = _service.FileExists;
        OnPropertyChanged(nameof(HasProviders));
        FileHint = _lastLoadError != null
            ? $"⚠ models.json 读取失败:{_lastLoadError} —— 点「编辑原始 JSON」修复"
            : HasFile
                ? $"共 {Providers.Count} 个服务商 / {Providers.Sum(p => p.ModelCount)} 个模型 · {_service.ModelsFile}"
                : "尚未创建 models.json —— 点「➕ 新增服务商」即可创建。修改后 pi 的 /model 会自动重新加载。";
        SelectedProvider = prev != null
            ? Providers.FirstOrDefault(x => x.Name.Equals(prev, StringComparison.Ordinal)) ?? Providers.FirstOrDefault()
            : Providers.FirstOrDefault();
        if (SelectedProvider == null)
            LoadModels(); // 无选中时也要清空模型区
    }

    partial void OnSelectedProviderChanged(ModelProviderRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedProvider));
        LoadModels();
    }

    partial void OnSelectedModelChanged(CustomModelRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedModel));
    }

    public bool HasSelectedModel => SelectedModel != null;

    private void LoadModels()
    {
        Models.Clear();
        SelectedModel = null;
        var row = SelectedProvider;
        var p = row?.Provider;
        if (row == null || p == null)
        {
            ProviderSummary = "";
            return;
        }
        foreach (var m in p.Models)
            Models.Add(new CustomModelRowViewModel { Model = m });
        OnPropertyChanged(nameof(HasModels));

        var summary = $"{row.BaseUrlText}  ·  {row.ApiText}  ·  Key:{row.KeyText}  ·  共 {row.ModelCount} 个模型";
        if (!string.IsNullOrEmpty(p.HeadersJson)) summary += "  ·  自定义请求头";
        if (!string.IsNullOrEmpty(p.CompatJson)) summary += "  ·  compat 兼容设置";
        ProviderSummary = summary;
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    // ---------- 服务商 ----------

    [RelayCommand]
    private async Task AddProviderAsync()
    {
        var edit = await Dialogs.EditProviderAsync(null);
        if (edit == null) return;
        try
        {
            _service.SaveProvider(edit, null);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
            return;
        }
        Refresh();
        SelectedProvider = Providers.FirstOrDefault(x => x.Name.Equals(edit.Name, StringComparison.Ordinal));
        Main?.SetStatus($"已新增服务商 {edit.Name}");
    }

    [RelayCommand]
    private async Task EditProviderAsync()
    {
        if (SelectedProvider == null) return;
        var p = SelectedProvider.Provider;
        var edit = await Dialogs.EditProviderAsync(p);
        if (edit == null) return;
        try
        {
            _service.SaveProvider(edit, p.Name);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
            return;
        }
        Refresh();
        SelectedProvider = Providers.FirstOrDefault(x => x.Name.Equals(edit.Name, StringComparison.Ordinal));
        Main?.SetStatus($"已更新服务商 {edit.Name}");
    }

    [RelayCommand]
    private async Task RemoveProviderAsync()
    {
        if (SelectedProvider == null) return;
        var p = SelectedProvider.Provider;
        var ok = await Dialogs.ConfirmAsync("删除服务商",
            $"删除「{p.Name}」及其 {p.Models.Count} 个模型条目?\n此操作直接修改 {_service.ModelsFile}。");
        if (!ok) return;
        try
        {
            _service.RemoveProvider(p.Name);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("删除失败", ex.Message);
            return;
        }
        Refresh();
        Main?.SetStatus($"已删除服务商 {p.Name}");
    }

    // ---------- 模型 ----------

    [RelayCommand]
    private async Task AddModelAsync()
    {
        if (SelectedProvider == null) return;
        var providerName = SelectedProvider.Name;
        var edit = await Dialogs.EditModelAsync(null);
        if (edit == null) return;
        try
        {
            _service.SaveModel(providerName, edit, null);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
            return;
        }
        Refresh();
        SelectedModel = Models.FirstOrDefault(x => x.Id.Equals(edit.Id.Trim(), StringComparison.Ordinal));
        Main?.SetStatus($"已在 {providerName} 下新增模型 {edit.Id.Trim()}");
    }

    [RelayCommand]
    private async Task EditModelAsync()
    {
        if (SelectedProvider == null || SelectedModel == null) return;
        var providerName = SelectedProvider.Name;
        var originalId = SelectedModel.Model.Id;
        var edit = await Dialogs.EditModelAsync(SelectedModel.Model);
        if (edit == null) return;
        try
        {
            _service.SaveModel(providerName, edit, originalId);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
            return;
        }
        Refresh();
        SelectedModel = Models.FirstOrDefault(x => x.Id.Equals(edit.Id.Trim(), StringComparison.Ordinal));
        Main?.SetStatus($"已更新模型 {edit.Id.Trim()}");
    }

    [RelayCommand]
    private async Task RemoveModelAsync()
    {
        if (SelectedProvider == null || SelectedModel == null) return;
        var providerName = SelectedProvider.Name;
        var modelId = SelectedModel.Model.Id;
        var ok = await Dialogs.ConfirmAsync("删除模型", $"从「{providerName}」删除模型 {modelId}?");
        if (!ok) return;
        try
        {
            _service.RemoveModel(providerName, modelId);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("删除失败", ex.Message);
            return;
        }
        Refresh();
        Main?.SetStatus($"已删除模型 {modelId}");
    }

    // ---------- 原始文件 ----------

    [RelayCommand]
    private async Task EditRawAsync()
    {
        var raw = _service.LoadRaw() ?? "{\n  \"providers\": {}\n}";
        var edited = await Dialogs.EditTextAsync(
            "编辑 models.json(原始 JSON)",
            raw,
            hint: "直接编辑整个文件。pi 的 modelOverrides 等扩展字段会原样保留。");
        if (edited == null) return;
        try
        {
            _service.SaveRaw(edited);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", "JSON 不合法,未写入文件:\n" + ex.Message);
            return;
        }
        Refresh();
        Main?.SetStatus("已保存 models.json");
    }

    [RelayCommand]
    private void OpenLocation()
    {
        var file = _service.ModelsFile;
        if (File.Exists(file))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{file}\"",
                UseShellExecute = true,
            });
        }
        else if (Directory.Exists(_env.AgentDir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_env.AgentDir}\"",
                UseShellExecute = true,
            });
        }
    }
}
