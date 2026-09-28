using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

/// <summary>消息列表行(SillyTavern 式):直接包装 PresetMessage,编辑即时写入模型。</summary>
public partial class MessageRowViewModel : ViewModelBase
{
    public required PresetMessage Message { get; init; }
    public required PresetEditorViewModel Editor { get; init; }

    public static readonly string[] Roles = { "system", "user", "assistant" };

    public string RoleDisplay => Message.RoleDisplay;

    public bool IsSystem => Message.Role == "system";
    public bool IsUser => Message.Role == "user";
    public bool IsAssistant => Message.Role == "assistant";

    /// <summary>压缩豁免说明(随角色变化)。system → 系统提示词层;user/AI → 每请求重拼。</summary>
    public string ImmunityNote => Message.Role switch
    {
        "system" => "系统提示词层 · 永不压缩",
        "user" => "用户消息 · 每请求重拼 · 永不压缩",
        _ => "AI消息 · 每请求重拼 · 永不压缩",
    };

    public string Role
    {
        get => Message.Role;
        set
        {
            if (Message.Role == value) return;
            Message.Role = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoleDisplay));
            OnPropertyChanged(nameof(IsSystem));
            OnPropertyChanged(nameof(IsUser));
            OnPropertyChanged(nameof(IsAssistant));
            OnPropertyChanged(nameof(ImmunityNote));
            Editor.NotifyDirty();
        }
    }

    public string Label
    {
        get => Message.Label;
        set
        {
            if (Message.Label == value) return;
            Message.Label = value;
            OnPropertyChanged();
            Editor.NotifyDirty();
        }
    }

    public string Content
    {
        get => Message.Content;
        set
        {
            if (Message.Content == value) return;
            Message.Content = value;
            OnPropertyChanged();
            Editor.NotifyDirty();
        }
    }

    public bool Enabled
    {
        get => Message.Enabled;
        set
        {
            if (Message.Enabled == value) return;
            Message.Enabled = value;
            OnPropertyChanged();
            Editor.NotifyDirty();
        }
    }

    [RelayCommand]
    private void MoveUp() => Editor.MoveMessage(this, -1);

    [RelayCommand]
    private void MoveDown() => Editor.MoveMessage(this, +1);

    [RelayCommand]
    private void Remove() => Editor.RemoveMessage(this);

    [RelayCommand]
    private async Task ExpandAsync()
    {
        var result = await Dialogs.EditTextAsync(
            $"编辑消息:{(string.IsNullOrWhiteSpace(Label) ? RoleDisplay : Label)}",
            Content,
            mono: false);
        if (result == null) return;
        Content = result;
    }
}

public partial class PresetEditorViewModel : ViewModelBase
{
    private readonly PresetService _service;
    private readonly PiEnvironment _env;

    public PresetInfo Info { get; set; } = null!;

    public Preset Preset => Info.Preset;

    public ObservableCollection<MessageRowViewModel> MessageRows { get; } = new();

    /// <summary>保存成功后触发,供列表同步。</summary>
    public event Action? Saved;

    [ObservableProperty]
    private bool _dirty;

    private int _revision;

    /// <summary>编辑修订号:任何字段/消息修改都会 +1(供预览实时刷新)。</summary>
    public int Revision
    {
        get => _revision;
        private set => SetProperty(ref _revision, value);
    }

    public PresetEditorViewModel(PresetService service, PiEnvironment env)
    {
        _service = service;
        _env = env;
    }

    public PresetEditorViewModel(PresetInfo info, PresetService service, PiEnvironment env)
    {
        _service = service;
        _env = env;
        Initialize(info);
    }

    public void Initialize(PresetInfo info)
    {
        Info = info;
        // 扩展/技能文本只在载入时初始化一次;Rebuild(消息增删/调序)不得重置未保存的输入
        ExtensionsText = string.Join("\n", Preset.Extensions);
        SkillsText = string.Join("\n", Preset.Skills);
        Rebuild();
    }

    private void Rebuild()
    {
        MessageRows.Clear();
        foreach (var m in Preset.Messages)
            MessageRows.Add(new MessageRowViewModel { Message = m, Editor = this });

        Dirty = false;
        OnPropertyChanged(nameof(Dir));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SessionName));
        OnPropertyChanged(nameof(OpeningMessage));
        OnPropertyChanged(nameof(McpConfig));
        OnPropertyChanged(nameof(ExtensionsExclusive));
        OnPropertyChanged(nameof(SkillsExclusive));
    }

    public void NotifyDirty()
    {
        Dirty = true;
        Revision++;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Preset.Extensions = SplitLines(ExtensionsText);
        Preset.Skills = SplitLines(SkillsText);
        try
        {
            _service.Save(Info);
            Dirty = false;
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("保存失败", ex.Message);
        }
    }

    private static List<string> ParseList(string text) =>
        text.Split(new[] { ',', '\n', ';', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---------- 消息操作(SillyTavern 式) ----------

    [RelayCommand]
    private void AddMessage(string role)
    {
        Preset.Messages.Add(new PresetMessage
        {
            Role = role,
            Label = role switch
            {
                "user" => "用户消息",
                "assistant" => "AI 消息",
                _ => "系统消息",
            },
            Content = "",
            Enabled = true,
        });
        Rebuild();
        Dirty = true;
        Revision++; // Rebuild 会重置 Dirty,这里补上修订号(供预览刷新/外部订阅)
    }

    public void RemoveMessage(MessageRowViewModel row)
    {
        Preset.Messages.Remove(row.Message);
        Rebuild();
        Dirty = true;
        Revision++;
    }

    public void MoveMessage(MessageRowViewModel row, int delta)
    {
        var idx = Preset.Messages.IndexOf(row.Message);
        var next = idx + delta;
        if (idx < 0 || next < 0 || next >= Preset.Messages.Count) return;
        (Preset.Messages[idx], Preset.Messages[next]) = (Preset.Messages[next], Preset.Messages[idx]);
        Rebuild();
        Dirty = true;
        Revision++;
    }

    // ---------- MCP ----------

    [RelayCommand]
    private async Task EditMcpAsync()
    {
        var file = McpConfig;
        if (string.IsNullOrWhiteSpace(file))
        {
            var name = await Dialogs.InputAsync("预设 MCP 配置", "文件名(保存在预设文件夹内)", "mcp.json");
            if (string.IsNullOrWhiteSpace(name)) return;
            file = name.Trim();
        }
        var content = _service.ReadFragment(Info, file) ?? "{\n  \"mcpServers\": {\n    // 本预设专属服务器;同名项可用 {\"disabled\":true} 覆盖全局\n  }\n}";
        var result = await Dialogs.EditTextAsync($"编辑:{file}", content);
        if (result == null) return;
        try
        {
            _service.WriteFragment(Info, file, result);
        }
        catch (Exception ex)
        {
            // 文件名非法/磁盘错误等:明确报错,不静默丢失编辑内容
            await Dialogs.InfoAsync("写入失败", $"{file} 未能写入:{ex.Message}\n\n编辑内容仍在窗口中,可修正文件名后重试。");
            return;
        }
        McpConfig = file;
        Dirty = true;
    }

    [RelayCommand]
    private void ClearMcp()
    {
        McpConfig = "";
    }

    [RelayCommand]
    private void OpenDir()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{Info.Directory}\"",
            UseShellExecute = true,
        });
    }

    // ---------- 属性直通 ----------

    public string Dir => Info.Directory;

    public string Name { get => Preset.Name; set { Preset.Name = value; OnPropertyChanged(); NotifyDirty(); } }
    public string Description { get => Preset.Description; set { Preset.Description = value; OnPropertyChanged(); NotifyDirty(); } }
    public string SessionName { get => Preset.SessionName; set { Preset.SessionName = value; OnPropertyChanged(); NotifyDirty(); } }
    public string OpeningMessage { get => Preset.OpeningMessage; set { Preset.OpeningMessage = value; OnPropertyChanged(); NotifyDirty(); } }
    public string McpConfig { get => Preset.McpConfig; set { Preset.McpConfig = value; OnPropertyChanged(); NotifyDirty(); } }

    public bool ExtensionsExclusive
    {
        get => Preset.ExtensionsMode == "exclusive";
        set { Preset.ExtensionsMode = value ? "exclusive" : "additive"; OnPropertyChanged(); NotifyDirty(); }
    }

    public bool SkillsExclusive
    {
        get => Preset.SkillsMode == "exclusive";
        set { Preset.SkillsMode = value ? "exclusive" : "additive"; OnPropertyChanged(); NotifyDirty(); }
    }

    private string _extensionsText = "";
    public string ExtensionsText { get => _extensionsText; set { SetProperty(ref _extensionsText, value); NotifyDirty(); } }

    private string _skillsText = "";
    public string SkillsText { get => _skillsText; set { SetProperty(ref _skillsText, value); NotifyDirty(); } }
}
