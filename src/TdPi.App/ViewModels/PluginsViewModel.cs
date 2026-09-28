using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class PluginRowViewModel : ViewModelBase
{
    public required PluginInfo Plugin { get; set; }

    public string Name => Plugin.DisplayName;
    public string KindText => Plugin.Kind switch
    {
        PluginKind.Package => "Pi 包",
        PluginKind.ExtensionEntry => "本地扩展",
        _ => "settings 路径",
    };
    public string ScopeText => Plugin.IsGlobal ? "全局" : "项目";
    public string Source => Plugin.Source;
    public string Detail => Plugin.Detail;
    public string ToggleLocation => Plugin.ToggleLocation;
    public bool Enabled => Plugin.Enabled;
    public bool Partial => Plugin.Partial;
    public PluginKind Kind => Plugin.Kind;
}

public partial class PluginsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly PluginService _service;
    private readonly PiCliRunner _cli;

    public MainViewModel? Main { get; set; }

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = new();

    [ObservableProperty]
    private PluginRowViewModel? _selected;

    [ObservableProperty]
    private bool _hasProject;

    [ObservableProperty]
    private string _cliOutput = "";

    [ObservableProperty]
    private bool _hasCliOutput;

    partial void OnCliOutputChanged(string value) => HasCliOutput = value.Length > 0;

    [ObservableProperty]
    private bool _cliBusy;

    [ObservableProperty]
    private string _emptyHint = "";

    public PluginsViewModel(PiEnvironment env, PluginService service, PiCliRunner cli)
    {
        _env = env;
        _service = service;
        _cli = cli;
    }

    public void Refresh()
    {
        Plugins.Clear();
        var project = Main?.CurrentProject;
        HasProject = !string.IsNullOrEmpty(project);
        foreach (var p in _service.ListPlugins(string.IsNullOrEmpty(project) ? null : project))
        {
            Plugins.Add(new PluginRowViewModel { Plugin = p });
        }
        EmptyHint = Plugins.Count == 0
            ? "暂无插件。Pi 包用「安装」添加(如 npm:pi-web-access),本地扩展放 ~/.pi/agent/extensions/。"
            : $"共 {Plugins.Count} 项。包的启停 = 空资源过滤对象形式;本地扩展 = 重命名 .disabled;修改后重启 pi 或 /reload 生效。";
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Selected == null) return;
        try
        {
            switch (Selected.Plugin.Kind)
            {
                case PluginKind.Package:
                    _service.TogglePackage(Selected.Plugin);
                    break;
                case PluginKind.ExtensionEntry:
                    _service.ToggleExtension(Selected.Plugin);
                    break;
                case PluginKind.SettingsPath:
                    _service.ToggleSettingsPath(Selected.Plugin);
                    break;
            }
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("操作失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        // 不再单独弹框询问安装范围:位置(全局/项目)与项目文件夹在表单「基本」区直接选择/指定
        var result = await Dialogs.InstallPackageAsync(Main?.CurrentProject);
        if (result == null) return;
        var proj = result.Value.Project;
        var global = proj.Length == 0;
        var source = QuoteCliArg(result.Value.Source);
        var args = global ? $"install {source}" : $"install --local {source}";
        await RunCliAsync(args, global ? _env.AgentDir : proj);
    }

    /// <summary>pi CLI 参数加引号(本地路径可能含空格;内嵌引号剥除)。</summary>
    private static string QuoteCliArg(string arg)
    {
        var safe = arg.Replace("\"", "");
        return safe.Contains(' ') ? $"\"{safe}\"" : safe;
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (Selected?.Plugin.Kind != PluginKind.Package) return;
        var ok = await Dialogs.ConfirmAsync("卸载包", $"pi remove {Selected.Source}\n同时移除其 settings 条目。继续?");
        if (!ok) return;
        var global = Selected.Plugin.IsGlobal;
        await RunCliAsync(
            global ? $"remove {QuoteCliArg(Selected.Source)}" : $"remove --local {QuoteCliArg(Selected.Source)}",
            global ? _env.AgentDir : (Main?.CurrentProject ?? _env.AgentDir));
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (Selected?.Plugin.Kind != PluginKind.Package) return;
        var global = Selected.Plugin.IsGlobal;
        if (global)
        {
            await RunCliAsync($"update {QuoteCliArg(Selected.Source)}", _env.AgentDir);
            return;
        }

        // pi CLI 的 update 无 --local,只能更新全局包;项目包用「卸载 → 重装」实现更新
        var ok = await Dialogs.ConfirmAsync("更新项目包",
            $"pi CLI 不支持按包更新项目本地包(update 无 --local)。\n\n将执行卸载后重装(等同于更新到最新版):\n  1. pi remove --local {Selected.Source}\n  2. pi install --local {Selected.Source}\n\n继续?");
        if (!ok) return;
        var proj = Main?.CurrentProject ?? _env.AgentDir;
        var source = QuoteCliArg(Selected.Source);
        CliBusy = true;
        CliOutput = $"$ pi remove --local {source}\n运行中...";
        try
        {
            var (code1, out1) = await _cli.RunAsync($"remove --local {source}", proj);
            var (code2, out2) = await _cli.RunAsync($"install --local {source}", proj);
            CliOutput = $"$ pi remove --local {source}\n{out1}\n(退出码 {code1})\n\n$ pi install --local {source}\n{out2}\n(退出码 {code2})"
                + (code2 == 0 ? "\n✓ 项目包已更新" : "\n⚠ 重装失败,请检查输出后手动 pi install --local");
            Refresh();
        }
        catch (Exception ex)
        {
            CliOutput = $"执行失败:{ex.Message}\n若包已被卸载,请手动执行 pi install --local {source}";
        }
        finally
        {
            CliBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        // update --extensions 仅作用于全局包(pi 限制);项目包请逐个用「更新」(卸载+重装)
        await RunCliAsync("update --extensions", _env.AgentDir);
        Main?.SetStatus("已更新全局包;项目本地包请逐个选中后点「更新」(卸载+重装)");
    }

    [RelayCommand]
    private void OpenLocation()
    {
        var target = Selected?.Plugin.EntryFile ?? Selected?.Plugin.ToggleLocation;
        if (target == null) return;
        if (File.Exists(target))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true,
            });
        }
        else if (Directory.Exists(target))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
        }
    }

    private async Task RunCliAsync(string args, string workDir)
    {
        if (CliBusy)
        {
            // 静默丢弃会让人以为点击无效;给出明确反馈
            Main?.SetStatus("上一条命令仍在执行,请等待完成后再试");
            return;
        }
        CliBusy = true;
        CliOutput = $"$ pi {args}\n运行中...";
        try
        {
            var (code, output) = await _cli.RunAsync(args, workDir);
            CliOutput = $"$ pi {args}\n{output}\n(退出码 {code})";
            Refresh();
        }
        catch (Exception ex)
        {
            CliOutput = $"执行失败:{ex.Message}";
        }
        finally
        {
            CliBusy = false;
        }
    }
}
