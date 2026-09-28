using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class PresetListItemViewModel : ViewModelBase
{
    public required PresetInfo Info { get; set; }
    public string Name => Info.Preset.Name;
    public string Description => Info.Preset.Description;
    public string Dir => Info.Directory;
}

public partial class PresetsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly PresetService _service;
    private readonly PiCliRunner _cli;
    private readonly SkillService _skills;
    private readonly McpService _mcp;
    private readonly PluginService _plugins;

    public MainViewModel? Main { get; set; }

    public ObservableCollection<PresetListItemViewModel> Presets { get; } = new();

    [ObservableProperty]
    private PresetListItemViewModel? _selected;

    [ObservableProperty]
    private PresetEditorViewModel? _editor;

    [ObservableProperty]
    private bool _hasEditor;

    [ObservableProperty]
    private bool _hasSelected;

    /// <summary>是否已选择项目(空状态提示条用)。</summary>
    [ObservableProperty]
    private bool _hasProject;

    // 命令行启动预览(供复制到任意终端手动启动)
    [ObservableProperty]
    private string _launchPreview = "";

    [ObservableProperty]
    private string _launchWarnings = "";

    [ObservableProperty]
    private CompiledLaunch? _lastCompiled;

    // 上下文预览
    [ObservableProperty]
    private string _systemBlockPreview = "";

    [ObservableProperty]
    private string _openingPreview = "";

    public ObservableCollection<PreviewMessageRowViewModel> ContextPreviewRows { get; } = new();

    [ObservableProperty]
    private string _contextSummary = "";

    [ObservableProperty]
    private string _mcpSummary = "";

    [ObservableProperty]
    private string _skillsSummary = "";

    public ObservableCollection<ExposureItemViewModel> McpExposure { get; } = new();
    public ObservableCollection<ExposureItemViewModel> SkillExposure { get; } = new();
    public ObservableCollection<ExposureItemViewModel> ExtensionExposure { get; } = new();

    public PresetsViewModel(PiEnvironment env, PresetService service, PiCliRunner cli,
        SkillService skills, McpService mcp, PluginService plugins)
    {
        _env = env;
        _service = service;
        _cli = cli;
        _skills = skills;
        _mcp = mcp;
        _plugins = plugins;
    }

    public void Refresh()
    {
        Presets.Clear();
        var project = Main?.CurrentProject ?? "";
        HasProject = !string.IsNullOrEmpty(project);
        foreach (var p in _service.ListPresets(project))
        {
            Presets.Add(new PresetListItemViewModel { Info = p });
        }
        Selected = null;
        Editor = null;
        HasEditor = false;
        HasSelected = false;

        UpdateLaunchPreview();
    }

    /// <summary>有未保存修改时先确认;返回 true = 可继续丢弃。用户发起的操作(新建/复制/删除/手动刷新)前调用。</summary>
    private async Task<bool> ConfirmDiscardDirtyAsync()
    {
        if (Editor?.Dirty != true) return true;
        return await Dialogs.ConfirmAsync("未保存的修改",
            $"预设「{Editor.Name}」有未保存的修改,继续将丢弃这些修改。确定?");
    }

    /// <summary>有未保存修改时切换选中:先确认,拒绝则弹回旧选中(保留未保存修改)。</summary>
    partial void OnSelectedChanged(PresetListItemViewModel? value)
    {
        if (_suppressSelection) return;

        // 同一预设:保留现有编辑器实例(内存中的未保存修改不丢)
        if (Editor != null && value != null
            && value.Dir.Equals(Editor.Info.Directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 切到另一个预设且当前编辑器有未保存修改 → 先确认
        if (Editor?.Dirty == true && value != null && !_forceSelection)
        {
            var pending = value;
            var prev = Presets.FirstOrDefault(p => p.Dir.Equals(Editor.Info.Directory, StringComparison.OrdinalIgnoreCase));
            // 先弹回旧选中(不重建编辑器),避免未确认就丢弃编辑状态
            if (prev != null)
            {
                _suppressSelection = true;
                Selected = prev;
                _suppressSelection = false;
            }
            _ = ConfirmThenSelectAsync(pending);
            return;
        }

        if (Editor != null)
        {
            Editor.PropertyChanged -= OnEditorPropertyChanged;
            Editor.Saved -= OnEditorSaved;
        }
        Editor = value == null ? null : BuildEditor(value.Info);
        HasEditor = Editor != null;
        HasSelected = value != null;
        if (Editor != null)
        {
            Editor.PropertyChanged += OnEditorPropertyChanged;
            Editor.Saved += OnEditorSaved;
        }
        UpdateLaunchPreview();
    }

    /// <summary>确认后跳转到目标预设;拒绝则什么都不做(选中已弹回旧项,编辑器未动)。</summary>
    private async Task ConfirmThenSelectAsync(PresetListItemViewModel pending)
    {
        if (await ConfirmDiscardDirtyAsync())
        {
            _forceSelection = true;
            Selected = pending;
            _forceSelection = false;
        }
    }

    private bool _forceSelection;

    /// <summary>编辑器保存后同步左侧列表,保持编辑器与选中不中断。</summary>
    private void OnEditorSaved()
    {
        if (Editor == null) return;
        var dir = Editor.Info.Directory;
        _suppressSelection = true;
        Presets.Clear();
        foreach (var p in _service.ListPresets(Main?.CurrentProject ?? ""))
        {
            Presets.Add(new PresetListItemViewModel { Info = p });
        }
        Selected = Presets.FirstOrDefault(p => p.Info.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase));
        _suppressSelection = false;
    }

    private bool _suppressSelection;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresetEditorViewModel.Dirty) or nameof(PresetEditorViewModel.Revision))
            UpdateLaunchPreview();
    }

    private PresetEditorViewModel BuildEditor(PresetInfo info)
    {
        // 深拷贝,编辑不影响磁盘,直到保存
        var json = System.Text.Json.JsonSerializer.Serialize(info.Preset);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Preset>(json) ?? new Preset();
        var editor = new PresetEditorViewModel(_service, _env);
        editor.Initialize(new PresetInfo
        {
            Preset = copy,
            Directory = info.Directory,
        });
        return editor;
    }

    private void UpdateLaunchPreview()
    {
        if (Selected == null)
        {
            LaunchPreview = "选择预设后显示命令行启动命令预览。";
            LaunchWarnings = "";
            HasLaunchWarnings = false;
            LastCompiled = null;
            SystemBlockPreview = "";
            OpeningPreview = "";
            ContextPreviewRows.Clear();
            ContextSummary = "";
            McpSummary = "";
            SkillsSummary = "";
            McpExposure.Clear();
            SkillExposure.Clear();
            ExtensionExposure.Clear();
            return;
        }
        try
        {
            var preset = Editor?.Preset ?? Selected.Info.Preset;
            var dir = Selected.Info.Directory;

            // ---- 工作目录 = 顶部当前项目(预设只存在于项目中) ----
            var launchDir = Main?.CurrentProject ?? "";
            if (launchDir.Length == 0)
            {
                LaunchPreview = "选择项目(顶部)后,显示命令行启动命令预览。";
                LaunchWarnings = "";
                HasLaunchWarnings = false;
                LastCompiled = null;
                SystemBlockPreview = "";
                OpeningPreview = "";
                ContextPreviewRows.Clear();
                ContextSummary = "";
                McpSummary = "";
                SkillsSummary = "";
                McpExposure.Clear();
                SkillExposure.Clear();
                ExtensionExposure.Clear();
                return;
            }

            // ---- 启动命令预览 ----
            var compiled = PresetCompiler.Compile(preset, dir, launchDir, _env);
            LastCompiled = compiled;
            LaunchPreview = compiled.CommandLine;
            var warnings = compiled.Warnings.Count > 0
                ? "⚠ " + string.Join("\n⚠ ", compiled.Warnings)
                : "";
            LaunchWarnings = warnings;
            HasLaunchWarnings = warnings.Length > 0;

            // ---- 上下文预览:三类预填信息的最终形态 ----
            BuildContextPreview(preset);

            BuildExposure(preset, dir);
        }
        catch (Exception ex)
        {
            LaunchPreview = "(编译失败)";
            LaunchWarnings = "编译失败:" + ex.Message;
            HasLaunchWarnings = true;
            // 清空上次成功编译的结果:避免复制按钮复制过期命令、面板显示旧预览
            LastCompiled = null;
            SystemBlockPreview = "";
            OpeningPreview = "";
            ContextPreviewRows.Clear();
            ContextSummary = "";
            McpSummary = "";
            SkillsSummary = "";
            McpExposure.Clear();
            SkillExposure.Clear();
            ExtensionExposure.Clear();
        }
    }

    [ObservableProperty]
    private bool _hasLaunchWarnings;

    /// <summary>构建暴露视图:MCP / 技能 / 扩展的最终生效清单。</summary>
    private void BuildExposure(Preset preset, string dir)
    {
        var project = Main!.CurrentProject;

        // ============ MCP ============
        McpExposure.Clear();
        var adapterInstalled = _mcp.IsAdapterInstalled(project);
        McpExposure.Add(new ExposureItemViewModel
        {
            Name = "pi-mcp-adapter",
            Detail = adapterInstalled ? "已安装,提供 mcp / mcpScript 工具" : "未安装 —— 预设的 --mcp-config 将无效",
            StatusText = adapterInstalled ? "就绪" : "未安装",
            StatusKind = adapterInstalled ? "on" : "off",
        });

        var envServers = _mcp.ListServers(project);
        var (presetMcp, presetMcpMissing, presetMcpError) = ParsePresetMcpServers(preset, dir);
        foreach (var s in envServers)
        {
            string status;
            string kind;
            var detail = $"{s.SourceScope} · {s.Transport}";
            if (s.Summary.Length > 0) detail += $" · {s.Summary}";

            if (presetMcp.TryGetValue(s.Name, out var def))
            {
                if (def["disabled"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d)
                {
                    status = "被预设禁用";
                    kind = "off";
                    detail += " · 预设 mcp.json 同名覆盖 disabled";
                }
                else
                {
                    status = "预设覆盖";
                    kind = "warn";
                    detail += " · 预设 mcp.json 同名重定义";
                }
            }
            else if (s.Disabled)
            {
                status = "已停用";
                kind = "off";
            }
            else
            {
                status = "可用";
                kind = "on";
            }
            McpExposure.Add(new ExposureItemViewModel
            {
                Name = s.Name,
                Detail = detail,
                StatusText = status,
                StatusKind = kind,
            });
        }
        foreach (var (name, _) in presetMcp)
        {
            if (envServers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            McpExposure.Add(new ExposureItemViewModel
            {
                Name = name,
                Detail = "仅本窗口(--mcp-config 新增)",
                StatusText = "预设新增",
                StatusKind = "info",
            });
        }
        if (presetMcpMissing || presetMcpError != null)
        {
            McpExposure.Add(new ExposureItemViewModel
            {
                Name = preset.McpConfig,
                Detail = "预设声明的 mcpConfig",
                StatusText = presetMcpError ?? "文件缺失",
                StatusKind = "off",
            });
        }
        var mcpOn = McpExposure.Count(m => m.StatusKind == "on");
        var mcpOff = McpExposure.Count(m => m.StatusText == "被预设禁用");
        var mcpNew = McpExposure.Count(m => m.StatusText == "预设新增");
        McpSummary = presetMcp.Count == 0
            ? $"环境服务器 {envServers.Count} 个(可用 {envServers.Count(s => !s.Disabled)});本预设不叠加 mcp.json"
            : $"环境 {envServers.Count} 个 + 预设专属 {presetMcp.Count} 个(新增 {mcpNew} · 覆盖禁用 {mcpOff})";

        // ============ 技能 ============
        SkillExposure.Clear();
        var exclusiveSkills = preset.SkillsMode.Equals("exclusive", StringComparison.OrdinalIgnoreCase);
        foreach (var s in _skills.ListSkills(project))
        {
            var available = s.Enabled && !exclusiveSkills;
            SkillExposure.Add(new ExposureItemViewModel
            {
                Name = s.Name,
                Detail = (s.Scope == SkillScope.Global ? "全局" : "项目") + (s.Enabled ? "" : " · 已停用")
                    + (s.DisableModelInvocation ? " · 仅手动(/skill:调用)" : ""),
                StatusText = exclusiveSkills ? "被 -ns 禁用" : available ? "可用" : "已停用",
                StatusKind = available ? "on" : "off",
            });
        }
        foreach (var sk in preset.Skills.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var exists = File.Exists(sk) || Directory.Exists(sk);
            SkillExposure.Add(new ExposureItemViewModel
            {
                Name = Path.GetFileName(sk.TrimEnd('/', '\\')),
                Detail = "预设 --skill 附加" + (exists ? "" : " · 路径不存在"),
                StatusText = exists ? "预设附加" : "缺失",
                StatusKind = exists ? "info" : "off",
            });
        }
        SkillsSummary = exclusiveSkills
            ? $"独占模式(--no-skills):环境技能全部禁用,仅预设附加 {preset.Skills.Count(s => !string.IsNullOrWhiteSpace(s))} 个"
            : $"叠加模式:环境可用技能 {SkillExposure.Count(s => s.StatusKind == "on")} 个 + 预设附加 {preset.Skills.Count(s => !string.IsNullOrWhiteSpace(s))} 个";

        // ============ 扩展 ============
        ExtensionExposure.Clear();
        var exclusiveExt = preset.ExtensionsMode.Equals("exclusive", StringComparison.OrdinalIgnoreCase);
        var needsMcp = !string.IsNullOrWhiteSpace(preset.McpConfig);
        foreach (var p in _plugins.ListPlugins(project))
        {
            var infra = IsInfrastructure(p, needsMcp);
            var loaded = p.Enabled && (!exclusiveExt || infra);
            var detail = p.Kind switch
            {
                PluginKind.Package => "Pi 包",
                PluginKind.ExtensionEntry => "本地扩展",
                _ => "settings 路径",
            } + (p.IsGlobal ? " · 全局" : " · 项目") + (p.Enabled ? "" : " · 已停用")
            + (exclusiveExt && infra ? " · 独占模式下保留" : "");
            ExtensionExposure.Add(new ExposureItemViewModel
            {
                Name = p.DisplayName,
                Detail = detail,
                StatusText = loaded ? "加载" : exclusiveExt ? "被 -ne 禁用" : "已停用",
                StatusKind = loaded ? "on" : "off",
            });
        }
        foreach (var e in preset.Extensions.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var path = Path.IsPathRooted(e) ? e : Path.GetFullPath(Path.Combine(dir, e));
            var exists = File.Exists(path) || Directory.Exists(path);
            ExtensionExposure.Add(new ExposureItemViewModel
            {
                Name = Path.GetFileName(path),
                Detail = "预设 -e 附加" + (exists ? "" : " · 路径不存在"),
                StatusText = exists ? "预设附加" : "缺失",
                StatusKind = exists ? "info" : "off",
            });
        }
    }

    /// <summary>构建上下文预览:启动后模型看到的完整消息结构(逐条)。</summary>
    private void BuildContextPreview(Preset preset)
    {
        // 系统块
        var block = PresetCompiler.BuildSystemBlock(preset);
        SystemBlockPreview = block.Length > 0
            ? block
            : "(无启用的系统消息 —— 在「消息列表」页勾选启用)";

        // 上下文消息 + 开场 + 真实对话分隔
        ContextPreviewRows.Clear();
        var ctxMsgs = PresetCompiler.GetContextMessages(preset);
        foreach (var m in ctxMsgs)
        {
            ContextPreviewRows.Add(new PreviewMessageRowViewModel
            {
                Role = m.Role,
                RoleDisplay = m.RoleDisplay,
                Content = m.Content,
                Note = m.Role == "user" ? "用户消息 · 每请求重拼 · 永不压缩" : "AI消息 · 每请求重拼 · 永不压缩",
            });
        }
        if (!string.IsNullOrWhiteSpace(preset.OpeningMessage))
        {
            ContextPreviewRows.Add(new PreviewMessageRowViewModel
            {
                Role = "real",
                RoleDisplay = "用户",
                Content = preset.OpeningMessage,
                Note = "开场消息(真实,位于对话区 —— 可被压缩,有意设计)",
            });
        }
        ContextPreviewRows.Add(new PreviewMessageRowViewModel
        {
            Role = "marker",
            RoleDisplay = "——",
            Content = "以下为真实对话(工具调用、你的消息、AI 回复 —— 这些会被压缩总结)",
            Note = "",
        });

        var sysCount = preset.Messages.Count(m => m.Enabled && m.Role == "system");
        ContextSummary = $"系统消息 {sysCount} 条(系统提示词层)+ 用户/AI 消息 {ctxMsgs.Count} 条(每请求重拼)" +
            (string.IsNullOrWhiteSpace(preset.OpeningMessage) ? "" : " + 开场消息 1 条(可牺牲)") +
            " —— 前两者永不被上下文压缩";
    }

    private static bool IsInfrastructure(PluginInfo p, bool needsMcp)
    {
        if (p.DisplayName.Equals("td-pi-preset", StringComparison.OrdinalIgnoreCase)) return true;
        if (needsMcp && p.DisplayName.Contains("pi-mcp-adapter", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>解析预设 mcpConfig 文件中的 mcpServers。</summary>
    private static (Dictionary<string, JsonObject> servers, bool missing, string? error) ParsePresetMcpServers(
        Preset preset, string dir)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(preset.McpConfig)) return (result, false, null);
        var path = Path.IsPathRooted(preset.McpConfig)
            ? preset.McpConfig
            : Path.GetFullPath(Path.Combine(dir, preset.McpConfig));
        if (!File.Exists(path)) return (result, true, null);
        try
        {
            var node = JsonHelper.TryLoadNode(path) as JsonObject;
            if (node?["mcpServers"] is JsonObject servers)
            {
                foreach (var (name, def) in servers)
                {
                    if (def is JsonObject o) result[name] = o;
                }
            }
            return (result, false, null);
        }
        catch (Exception ex)
        {
            return (result, false, ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshCmd()
    {
        if (!await ConfirmDiscardDirtyAsync()) return;
        Refresh();
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        var project = Main?.CurrentProject;
        if (string.IsNullOrEmpty(project))
        {
            await Dialogs.InfoAsync("需要项目", "预设只存在于项目中 —— 请先在顶部选择项目目录。");
            return;
        }
        if (!await ConfirmDiscardDirtyAsync()) return;
        var name = await Dialogs.InputAsync("新建预设", "预设名(成为文件夹名)", "my-preset");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _service.Create(name.Trim(), project);
            Refresh();
            var created = Presets.FirstOrDefault(p =>
                p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (created != null) Selected = created;
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("创建失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (Selected == null) return;
        if (!await ConfirmDiscardDirtyAsync()) return;
        var name = await Dialogs.InputAsync("复制预设", "新预设名", Selected.Name + "-copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _service.Duplicate(Selected.Info, name.Trim());
            Refresh();
            var created = Presets.FirstOrDefault(p => p.Name == name.Trim());
            if (created != null) Selected = created;
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("复制失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected == null) return;
        var ok = await Dialogs.ConfirmAsync("删除预设",
            $"确定删除预设 {Selected.Name}?\n{Selected.Dir}\n(整文件夹删除,不可恢复)");
        if (!ok) return;
        if (!await ConfirmDiscardDirtyAsync()) return;
        try
        {
            _service.Delete(Selected.Info);
            Refresh();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("删除失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyCmdAsync()
    {
        if (LastCompiled == null) return;
        try
        {
            if (Dialogs.Owner?.Clipboard is { } clip)
                await clip.SetTextAsync(LastCompiled.CommandLine);
        }
        catch (Exception ex)
        {
            Main?.SetStatus($"复制失败:{ex.Message}");
        }
    }
}
