using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class McpServerRowViewModel : ViewModelBase
{
    public required McpServerInfo Server { get; set; }

    public string Name => Server.Name;
    public string Transport => Server.Transport;
    public string Summary => Server.Summary;
    public string SourceScope => Server.SourceScope;
    public string SourceFile => Server.SourceFile;
    public bool Disabled => Server.Disabled;
    public bool Enabled => !Server.Disabled;
    public string DirectTools => Server.DirectTools;
    public bool FromProject => Server.FromProject;
}

public partial class McpViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly McpService _service;
    private readonly PiCliRunner _cli;

    public MainViewModel? Main { get; set; }

    public ObservableCollection<McpServerRowViewModel> Servers { get; } = new();

    [ObservableProperty]
    private McpServerRowViewModel? _selected;

    [ObservableProperty]
    private bool _hasProject;

    [ObservableProperty]
    private bool _adapterInstalled;

    [ObservableProperty]
    private string _adapterStatus = "";

    [ObservableProperty]
    private string _cliOutput = "";

    [ObservableProperty]
    private bool _hasCliOutput;

    partial void OnCliOutputChanged(string value) => HasCliOutput = value.Length > 0;

    [ObservableProperty]
    private bool _cliBusy;

    public McpViewModel(PiEnvironment env, McpService service, PiCliRunner cli)
    {
        _env = env;
        _service = service;
        _cli = cli;
    }

    public void Refresh()
    {
        Servers.Clear();
        var project = Main?.CurrentProject;
        HasProject = !string.IsNullOrEmpty(project);
        foreach (var s in _service.ListServers(string.IsNullOrEmpty(project) ? null : project))
        {
            Servers.Add(new McpServerRowViewModel { Server = s });
        }
        AdapterInstalled = _service.IsAdapterInstalled(string.IsNullOrEmpty(project) ? null : project);
        AdapterStatus = AdapterInstalled
            ? "已安装 pi-mcp-adapter。"
            : "未检测到 pi-mcp-adapter 包 —— MCP 工具由它提供,请先安装。";
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Selected == null) return;
        try
        {
            _service.SetDisabled(Selected.Server, !Selected.Server.Disabled,
                string.IsNullOrEmpty(Main?.CurrentProject) ? null : Main!.CurrentProject);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("操作失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        // 不再单独弹框询问写入位置:位置(全局/项目)与项目文件夹在表单「基本」区直接选择/指定
        var template = @"{
  ""command"": ""npx"",
  ""args"": [""-y"", ""some-mcp-server""],
  ""env"": {}
}";
        var result = await Dialogs.AddMcpServerAsync(Main?.CurrentProject, template,
            "stdio:command/args/env;远程:url(可加 headers/auth)。disabled:true = 停用。");
        if (result == null) return;
        try
        {
            var def = McpService.ParseDefinition(result.Value.Json);
            var global = result.Value.Project.Length == 0;
            _service.AddServer(result.Value.Name, def, global, result.Value.Project);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("添加失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected == null) return;
        var json = await Dialogs.EditTextAsync(
            $"编辑:{Selected.Name}({Selected.SourceScope})",
            Selected.Server.DefinitionJson);
        if (json == null) return;
        try
        {
            var def = McpService.ParseDefinition(json);
            _service.UpdateServer(Selected.Server, def,
                string.IsNullOrEmpty(Main?.CurrentProject) ? null : Main!.CurrentProject);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected == null) return;
        var ok = await Dialogs.ConfirmAsync("移除服务器",
            $"确定从所有配置层移除 {Selected.Name}?\n({Selected.SourceFile} 等)");
        if (!ok) return;
        try
        {
            _service.RemoveServer(Selected.Server,
                string.IsNullOrEmpty(Main?.CurrentProject) ? null : Main!.CurrentProject);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("移除失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenConfig()
    {
        var file = Selected?.Server.SourceFile ?? _env.SharedGlobalMcpFile;
        if (Selected == null && !File.Exists(file))
        {
            // 项目为空时只建全局文件(避免在进程工作目录生成游离的 .mcp.json)
            var project = Main?.CurrentProject;
            _service.EnsureSharedFiles(string.IsNullOrEmpty(project) ? null : project);
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{file}\"",
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private async Task InstallAdapterAsync()
    {
        await RunCliAsync("install npm:pi-mcp-adapter");
    }

    [RelayCommand]
    private async Task UpdateAdapterAsync()
    {
        await RunCliAsync("update npm:pi-mcp-adapter");
    }

    private async Task RunCliAsync(string args)
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
            var (code, output) = await _cli.RunAsync(args, _env.AgentDir);
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
