using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class SkillRowViewModel : ViewModelBase
{
    public required SkillInfo Skill { get; set; }

    public string Name => Skill.Name;
    public string Description => Skill.Description;
    public string ScopeText => Skill.Scope == SkillScope.Global ? "全局" : "项目";
    public string Source => Skill.SourceRoot;
    public string Path => Skill.SkillFile;

    /// <summary>技能实际所在的目录(项目技能在此显示自己的目录)。</summary>
    public string Directory => Skill.Directory;
    public bool Dmi => Skill.DisableModelInvocation;
    public bool Invalid => !Skill.IsValid;
    public string InvalidReason => Skill.InvalidReason;
}

public partial class SkillsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly SkillService _service;

    public MainViewModel? Main { get; set; }

    /// <summary>全局技能(所有项目共享,~/.pi/agent/skills)。</summary>
    public ObservableCollection<SkillRowViewModel> GlobalSkills { get; } = new();

    /// <summary>项目技能(当前项目,.pi/skills 与 .agents/skills)。</summary>
    public ObservableCollection<SkillRowViewModel> ProjectSkills { get; } = new();

    [ObservableProperty]
    private SkillRowViewModel? _selected;

    [ObservableProperty]
    private bool _hasProject;

    /// <summary>全局分区提示:数量 + 全局目录。</summary>
    [ObservableProperty]
    private string _globalHint = "";

    /// <summary>项目分区提示:数量或未选项目说明。</summary>
    [ObservableProperty]
    private string _projectHint = "";

    /// <summary>项目技能实际存在的目录(每行一个)。</summary>
    [ObservableProperty]
    private string _projectDirs = "";

    public SkillsViewModel(PiEnvironment env, SkillService service)
    {
        _env = env;
        _service = service;
    }

    public void Refresh()
    {
        GlobalSkills.Clear();
        ProjectSkills.Clear();
        var project = Main?.CurrentProject;
        HasProject = !string.IsNullOrEmpty(project);
        foreach (var s in _service.ListSkills(HasProject ? project : null))
        {
            var row = new SkillRowViewModel { Skill = s };
            if (s.Scope == SkillScope.Global) GlobalSkills.Add(row);
            else ProjectSkills.Add(row);
        }

        GlobalHint = $"共 {GlobalSkills.Count} 个 · {_env.GlobalSkillsDir}";
        if (!HasProject)
        {
            ProjectHint = "未选择项目 —— 在顶部选择项目目录后,这里显示项目技能。";
            ProjectDirs = "";
        }
        else
        {
            ProjectHint = $"共 {ProjectSkills.Count} 个";
            var dirs = new[]
                {
                    _env.ProjectSkillsDir(project!),
                    _env.ProjectAgentsSkillsDir(project!),
                }
                .Where(Directory.Exists)
                .ToList();
            ProjectDirs = dirs.Count > 0 ? string.Join("\n", dirs) : "(技能目录尚未创建,新建项目技能后自动生成)";
        }
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Selected == null) return;
        try
        {
            _service.Toggle(Selected.Skill);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("操作失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected == null) return;
        try
        {
            var content = await File.ReadAllTextAsync(Selected.Path);
            var result = await Dialogs.EditTextAsync($"编辑技能:{Selected.Name}", content);
            if (result != null)
            {
                await File.WriteAllTextAsync(Selected.Path, result);
                Refresh();
            }
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("编辑失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        // 不再单独弹框询问范围:位置(全局/项目)与项目文件夹在表单「基本」区直接选择/指定
        var result = await Dialogs.CreateSkillAsync(Main?.CurrentProject);
        if (result == null) return;
        try
        {
            var root = result.Value.Project.Length > 0
                ? _env.ProjectSkillsDir(result.Value.Project)
                : _env.GlobalSkillsDir;
            _service.Create(root, result.Value.Name, result.Value.Desc);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("创建失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected == null) return;
        var ok = await Dialogs.ConfirmAsync("删除技能",
            $"确定删除技能 {Selected.Name}?\n{Selected.Path}\n此操作会删除技能整个文件夹,不可恢复。");
        if (!ok) return;
        try
        {
            _service.Delete(Selected.Skill);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("删除失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{Selected.Path}\"",
            UseShellExecute = true,
        });
    }
}
