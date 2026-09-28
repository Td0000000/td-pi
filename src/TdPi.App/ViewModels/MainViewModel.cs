using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly AppSettings _settings;
    private readonly PresetService _presetService;

    public SkillsViewModel Skills { get; }
    public McpViewModel Mcp { get; }
    public PluginsViewModel Plugins { get; }
    public ModelsViewModel Models { get; }
    public PresetsViewModel Presets { get; }
    public PresetLauncherViewModel Launcher { get; }
    public SessionsViewModel Sessions { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private string _currentProject = "";

    /// <summary>是否已选择项目(各页面空状态提示/徽章用)。</summary>
    public bool HasProject => !string.IsNullOrWhiteSpace(CurrentProject);

    /// <summary>项目切换时徽章短暂高亮。</summary>
    [ObservableProperty]
    private bool _projectBadgeHighlight;

    [ObservableProperty]
    private string _statusText = "就绪";

    public ObservableCollection<string> RecentProjects { get; } = new();

    public MainViewModel(
        PiEnvironment env,
        AppSettings settings,
        SkillsViewModel skills,
        McpViewModel mcp,
        PluginsViewModel plugins,
        ModelsViewModel models,
        PresetsViewModel presets,
        PresetLauncherViewModel launcher,
        SessionsViewModel sessions,
        DiagnosticsViewModel diagnostics,
        PresetService presetService)
    {
        _env = env;
        _settings = settings;
        _presetService = presetService;
        Skills = skills;
        Mcp = mcp;
        Plugins = plugins;
        Models = models;
        Presets = presets;
        Launcher = launcher;
        Sessions = sessions;
        Diagnostics = diagnostics;
        skills.Main = this;
        mcp.Main = this;
        plugins.Main = this;
        models.Main = this;
        presets.Main = this;
        launcher.Main = this;
        sessions.Main = this;

        foreach (var p in settings.RecentProjects) RecentProjects.Add(p);
        var project = settings.LastProject;
        if (string.IsNullOrEmpty(project))
        {
            project = env.GetKnownProjects().FirstOrDefault().Path;
        }
        if (!string.IsNullOrEmpty(project))
        {
            CurrentProject = project;
        }
        CurrentPage = Presets;
    }

    partial void OnCurrentProjectChanged(string value)
    {
        OnPropertyChanged(nameof(HasProject));
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusText = "未选择项目 —— 项目级功能不可用";
            Skills.Refresh();
            Mcp.Refresh();
            Plugins.Refresh();
            Presets.Refresh();
            Launcher.Refresh();
            return;
        }
        _settings.TouchProject(value);
        if (!RecentProjects.Contains(value))
        {
            RecentProjects.Insert(0, value);
        }
        Skills.Refresh();
        Mcp.Refresh();
        Plugins.Refresh();
        Presets.Refresh();
        Launcher.Refresh();
        Diagnostics.Refresh();
        StatusText = $"项目:{value}";
        FlashProjectBadge();
    }

    private async void FlashProjectBadge()
    {
        ProjectBadgeHighlight = true;
        try { await Task.Delay(1200); } finally { ProjectBadgeHighlight = false; }
    }

    /// <summary>选择项目文件夹(顶栏「浏览」与各页空状态提示条共用)。</summary>
    [RelayCommand]
    private async Task BrowseProjectAsync()
    {
        var path = await Dialogs.PickFolderAsync("选择项目目录");
        if (!string.IsNullOrEmpty(path)) CurrentProject = path;
    }

    /// <summary>管理项目历史:移除单个/清空全部;可选同步删除被移除项目的遗留项目预设;移除当前项目时一并清空当前选择。</summary>
    [RelayCommand]
    private async Task ManageProjectsAsync()
    {
        var result = await Dialogs.ManageProjectsAsync(RecentProjects.ToList(), CurrentProject);
        if (result == null) return;

        // 同步删除被移除项目遗留的项目预设(.pi/td-pi)
        if (result.DeletePresets && result.Removed.Count > 0)
        {
            var errors = new List<string>();
            foreach (var p in result.Removed)
            {
                var err = _presetService.DeleteProjectPresets(p);
                if (err != null) errors.Add($"{p}\n  {err}");
            }
            if (errors.Count > 0)
                await Dialogs.InfoAsync("部分项目预设删除失败", string.Join("\n\n", errors));
        }

        var currentRemoved = CurrentProject.Length > 0
            && !result.Remaining.Contains(CurrentProject, StringComparer.OrdinalIgnoreCase);
        RecentProjects.Clear();
        foreach (var p in result.Remaining) RecentProjects.Add(p);
        _settings.SetRecentProjects(result.Remaining, CurrentProject);
        if (currentRemoved)
        {
            CurrentProject = ""; // 触发各页刷新 + 未选项目提示条
        }
    }

    [RelayCommand]
    private void NavSkills() => CurrentPage = Skills;

    [RelayCommand]
    private void NavMcp() => CurrentPage = Mcp;

    [RelayCommand]
    private void NavPlugins() => CurrentPage = Plugins;

    [RelayCommand]
    private void NavModels()
    {
        Models.Refresh(); // 进入页面即重新读取 models.json(可能被 pi 或手工修改过)
        CurrentPage = Models;
    }

    [RelayCommand]
    private void NavPresets() => CurrentPage = Presets;

    [RelayCommand]
    private void NavLauncher()
    {
        Launcher.Refresh(); // 进入页面即刷新(预设可能刚在管理页新建)
        CurrentPage = Launcher;
    }

    [RelayCommand]
    private void NavSessions()
    {
        Sessions.Refresh(); // 进入页面即重新扫描(会话可能随时新增)
        CurrentPage = Sessions;
    }

    [RelayCommand]
    private void NavDiagnostics()
    {
        Diagnostics.Refresh(); // 进入页面即刷新(路径/扩展版本可能已变化)
        CurrentPage = Diagnostics;
    }

    /// <summary>底部状态栏文字(各页面操作反馈用)。</summary>
    public void SetStatus(string text)
    {
        StatusText = text;
    }
}
