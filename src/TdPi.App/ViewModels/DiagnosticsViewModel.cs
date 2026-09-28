using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly ExtensionDeployer _deployer;
    private readonly PiCliRunner _cli;

    [ObservableProperty]
    private string _piPath = "";

    [ObservableProperty]
    private string _piVersion = "";

    [ObservableProperty]
    private string _agentDir = "";

    [ObservableProperty]
    private string _appData = "";

    [ObservableProperty]
    private string _nodePath = "";

    [ObservableProperty]
    private string _terminal = "";

    [ObservableProperty]
    private string _extensionStatus = "";

    [ObservableProperty]
    private bool _extensionInstalled;

    [ObservableProperty]
    private string _cliOutput = "";

    [ObservableProperty]
    private bool _hasCliOutput;

    partial void OnCliOutputChanged(string value) => HasCliOutput = value.Length > 0;

    [ObservableProperty]
    private bool _cliBusy;

    public ObservableCollection<string> KnownProjects { get; } = new();

    public DiagnosticsViewModel(PiEnvironment env, ExtensionDeployer deployer, PiCliRunner cli)
    {
        _env = env;
        _deployer = deployer;
        _cli = cli;
    }

    public void Refresh()
    {
        PiPath = _env.PiCommand ?? "未找到(请确认 pi 在 PATH 中)";
        PiVersion = _env.GetPiVersion() ?? "";
        AgentDir = _env.AgentDir;
        AppData = _env.AppDataDir;
        NodePath = _env.NodePath ?? "未找到";
        Terminal = _env.WindowsTerminal != null ? $"Windows Terminal({_env.WindowsTerminal})" : "cmd(未检测到 wt)";

        var installed = _deployer.GetInstalledVersion();
        var embedded = _deployer.GetEmbeddedVersion();
        ExtensionInstalled = installed != null;
        ExtensionStatus = installed == null
            ? "未安装 td-pi 预设扩展"
            : installed == embedded
                ? $"已安装 v{installed}(最新)"
                : $"已安装 v{installed},可更新到 v{embedded}";

        KnownProjects.Clear();
        foreach (var (path, time) in _env.GetKnownProjects())
        {
            KnownProjects.Add($"{path}    (最近使用:{time:yyyy-MM-dd HH:mm})");
        }
    }

    [RelayCommand]
    private async Task DeployExtensionAsync()
    {
        try
        {
            _deployer.Deploy();
            Refresh();
            await Dialogs.InfoAsync("部署完成",
                $"已写入 {_env.TdPiExtensionFile}\n新开 pi 会话即可注入预设(运行中的会话需 /reload)。\n注意:预设仅经管理器「启动预设」页启动时生效。");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("部署失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenAgentDir()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_env.AgentDir}\"",
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private async Task UpdatePiAsync()
    {
        if (CliBusy) return;
        CliBusy = true;
        CliOutput = "$ pi update\n运行中...";
        try
        {
            var (code, output) = await _cli.RunAsync("update", _env.AgentDir);
            CliOutput = $"$ pi update\n{output}\n(退出码 {code})";
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
