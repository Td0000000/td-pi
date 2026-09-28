using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

/// <summary>
/// 启动预设页:点击查看当前项目的预设详情,并在此一键启动。
/// 启动时强制以顶部当前项目为工作目录(终端自动进入项目目录)。
/// </summary>
public partial class PresetLauncherViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly PresetService _service;
    private readonly LaunchService _launcher;

    public MainViewModel? Main { get; set; }

    public ObservableCollection<PresetListItemViewModel> Presets { get; } = new();

    [ObservableProperty]
    private PresetListItemViewModel? _selected;

    [ObservableProperty]
    private bool _hasSelected;

    /// <summary>是否已选择项目(未选时整页不可用)。</summary>
    [ObservableProperty]
    private bool _hasProject;

    // ---------- 详情 ----------

    [ObservableProperty]
    private string _presetName = "";

    [ObservableProperty]
    private string _presetDescription = "";

    [ObservableProperty]
    private string _sessionNameText = "(预设名)";

    [ObservableProperty]
    private string _presetDirectory = "";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _systemBlockPreview = "";

    public ObservableCollection<PreviewMessageRowViewModel> ContextPreviewRows { get; } = new();

    // ---------- 启动 ----------

    [ObservableProperty]
    private string _launchPreview = "";

    [ObservableProperty]
    private string _launchWarnings = "";

    [ObservableProperty]
    private bool _hasLaunchWarnings;

    public PresetLauncherViewModel(PiEnvironment env, PresetService service, LaunchService launcher)
    {
        _env = env;
        _service = service;
        _launcher = launcher;
    }

    public void Refresh()
    {
        var project = Main?.CurrentProject ?? "";
        HasProject = !string.IsNullOrEmpty(project);

        Presets.Clear();
        foreach (var p in _service.ListPresets(project))
        {
            Presets.Add(new PresetListItemViewModel { Info = p });
        }

        // 尽量保持原选中(刷新后同名预设仍存在)
        var keep = Selected?.Name;
        Selected = null;
        HasSelected = false;
        if (keep != null)
        {
            Selected = Presets.FirstOrDefault(p => p.Name.Equals(keep, StringComparison.OrdinalIgnoreCase));
        }
        if (Selected == null) ClearDetail();
    }

    partial void OnSelectedChanged(PresetListItemViewModel? value)
    {
        HasSelected = value != null;
        if (value == null) { ClearDetail(); return; }
        BuildDetail(value.Info);
    }

    private void ClearDetail()
    {
        PresetName = "";
        PresetDescription = "";
        SessionNameText = "(预设名)";
        PresetDirectory = "";
        SummaryText = "";
        SystemBlockPreview = "";
        ContextPreviewRows.Clear();
        LaunchPreview = "选择预设后显示启动预览。";
        LaunchWarnings = "";
        HasLaunchWarnings = false;
    }

    private void BuildDetail(PresetInfo info)
    {
        var preset = info.Preset;
        PresetName = preset.Name;
        PresetDescription = preset.Description;
        SessionNameText = string.IsNullOrWhiteSpace(preset.SessionName) ? preset.Name : preset.SessionName.Trim();
        PresetDirectory = info.Directory;

        // ① 系统提示词注入块
        var block = PresetCompiler.BuildSystemBlock(preset);
        SystemBlockPreview = block.Length > 0 ? block : "(无启用的系统消息)";

        // ② 上下文消息 + 开场
        ContextPreviewRows.Clear();
        var ctxMsgs = PresetCompiler.GetContextMessages(preset);
        foreach (var m in ctxMsgs)
        {
            ContextPreviewRows.Add(new PreviewMessageRowViewModel
            {
                Role = m.Role,
                RoleDisplay = m.RoleDisplay,
                Content = m.Content,
                Note = string.IsNullOrWhiteSpace(m.Label) ? "" : m.Label,
            });
        }
        if (!string.IsNullOrWhiteSpace(preset.OpeningMessage))
        {
            ContextPreviewRows.Add(new PreviewMessageRowViewModel
            {
                Role = "real",
                RoleDisplay = "用户",
                Content = preset.OpeningMessage,
                Note = "开场消息",
            });
        }

        var sysCount = preset.Messages.Count(m => m.Enabled && m.Role == "system");
        SummaryText = $"系统消息 {sysCount} 条(系统提示词层,永不压缩)+ 用户/AI 消息 {ctxMsgs.Count} 条(每请求重拼,永不压缩)" +
            (string.IsNullOrWhiteSpace(preset.OpeningMessage) ? "" : " + 开场消息 1 条");

        UpdateLaunchPreview(preset, info.Directory);
    }

    private void UpdateLaunchPreview(Preset preset, string dir)
    {
        var project = Main?.CurrentProject ?? "";
        if (project.Length == 0)
        {
            LaunchPreview = "";
            LaunchWarnings = "";
            HasLaunchWarnings = false;
            return;
        }
        try
        {
            var compiled = PresetCompiler.Compile(preset, dir, project, _env);
            LaunchPreview = compiled.CommandLine;
            LaunchWarnings = compiled.Warnings.Count > 0
                ? "⚠ " + string.Join("\n⚠ ", compiled.Warnings)
                : "";
            HasLaunchWarnings = compiled.Warnings.Count > 0;
        }
        catch (Exception ex)
        {
            LaunchPreview = "";
            LaunchWarnings = "编译失败:" + ex.Message;
            HasLaunchWarnings = true;
        }
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    /// <summary>
    /// 启动预设:工作目录强制为顶部当前项目(终端自动进入项目目录)。
    /// </summary>
    [RelayCommand]
    private async Task LaunchAsync()
    {
        var project = Main?.CurrentProject;
        if (string.IsNullOrEmpty(project))
        {
            await Dialogs.InfoAsync("需要项目", "请先在顶部选择项目目录 —— 启动预设会自动进入项目目录。");
            return;
        }
        if (Selected == null) return;
        var info = Selected.Info;
        try
        {
            var compiled = PresetCompiler.Compile(info.Preset, info.Directory, project, _env);
            if (compiled.Warnings.Count > 0)
            {
                var go = await Dialogs.ConfirmAsync("启动前警告",
                    string.Join("\n", compiled.Warnings) + "\n\n仍然启动?");
                if (!go) return;
            }
            _launcher.Launch(compiled);
            Main?.SetStatus($"已启动预设 {info.Preset.Name}(项目:{project})");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("启动失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyCmdAsync()
    {
        if (Selected == null || LaunchPreview.Length == 0) return;
        try
        {
            if (Dialogs.Owner?.Clipboard is { } clip)
                await clip.SetTextAsync(LaunchPreview);
        }
        catch (Exception ex)
        {
            Main?.SetStatus($"复制失败:{ex.Message}");
        }
    }
}
