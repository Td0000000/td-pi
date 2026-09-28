using System.Text.Json.Serialization;
using TdPi.Core.Services;

namespace TdPi.Core.Models;

/// <summary>
/// 预设消息条目(SillyTavern 式 Prompt 管理)。
/// role = system → 系统提示词(永不压缩)
/// role = user / assistant → 上下文消息,经 td-pi-preset 扩展的 context 事件在每次 LLM 调用前
///   重新拼装到真实对话之前 —— 不写入会话,不是会话条目,因此永远不被上下文压缩。
/// </summary>
public class PresetMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>system | user | assistant</summary>
    public string Role { get; set; } = "system";

    /// <summary>条目标题(注入系统提示词块时作为节标题,可空)。</summary>
    public string Label { get; set; } = "";

    /// <summary>内容(内联存储,自由编辑)。</summary>
    public string Content { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string RoleDisplay => Role switch
    {
        "user" => "用户",
        "assistant" => "AI",
        _ => "系统",
    };
}

/// <summary>预设中的一个提示词片段(旧模型,仅用于迁移兼容)。</summary>
public class PresetFragment
{
    /// <summary>相对预设文件夹的文件路径(或绝对路径)。</summary>
    public string File { get; set; } = "";

    /// <summary>注入时使用的节标题。为空时取文件名。</summary>
    public string Label { get; set; } = "";

    /// <summary>是否注入。false = 保留定义但不注入(可选择注入或不注入)。</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label)
        ? System.IO.Path.GetFileNameWithoutExtension(File)
        : Label;
}

/// <summary>td-pi 预设(preset.json)。</summary>
public class Preset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>[已废弃] v1.4.1 起预设不再指定模型,一律用 pi 默认模型/用户选择;字段仅为旧 preset.json 反序列化兼容,不再写出与使用。</summary>
    public string Model { get; set; } = "";

    /// <summary>-n 会话显示名。为空时用预设名。</summary>
    public string SessionName { get; set; } = "";

    /// <summary>
    /// 消息列表(SillyTavern 式,规范存储):
    /// system 条目 → 系统提示词;user/assistant 条目 → 上下文消息经 context 事件注入。
    /// 顺序自由、逐条开关、内容内联可自由编辑。
    /// </summary>
    public List<PresetMessage> Messages { get; set; } = new();

    /// <summary>系统提示词注入片段(旧模型,加载时迁移到 Messages,保存后不再写出)。</summary>
    public List<PresetFragment> System { get; set; } = new();

    /// <summary>AI 消息预填(以"回复示例文本"形式进入系统提示词层,等效达成且永不被压缩)。</summary>
    public List<PresetFragment> AssistantExamples { get; set; } = new();

    /// <summary>跨预设共享片段库引用(../fragments/x.md)。</summary>
    public List<PresetFragment> SharedFragments { get; set; } = new();

    /// <summary>用户消息预填(开场白)。走 argv 位置参数 —— 有意可牺牲:会被压缩,关键信息不要放这里。</summary>
    public string OpeningMessage { get; set; } = "";

    /// <summary>附加扩展(-e)。additive=叠加在全局之上;exclusive=仅这些(基础设施除外)。</summary>
    public List<string> Extensions { get; set; } = new();

    public string ExtensionsMode { get; set; } = "additive";

    /// <summary>附加技能(--skill)。模式含义同上。</summary>
    public List<string> Skills { get; set; } = new();

    public string SkillsMode { get; set; } = "additive";

    /// <summary>预设专属 MCP 配置文件(--mcp-config),需安装 pi-mcp-adapter。支持用 {"disabled":true} 覆盖全局 server。</summary>
    public string McpConfig { get; set; } = "";

    public string Notes { get; set; } = "";

    [JsonIgnore]
    public bool HasAnyInjection => Messages.Any(m => m.Enabled);

    [JsonIgnore]
    public bool HasContextMessages => Messages.Any(m => m.Enabled && m.Role != "system");
}

/// <summary>预设 + 所在位置信息(预设只存在于项目中:&lt;project&gt;/.pi/td-pi/presets/&lt;name&gt;/)。</summary>
public class PresetInfo
{
    public required Preset Preset { get; set; }
    public required string Directory { get; set; }
}

/// <summary>编译结果。</summary>
public class CompiledLaunch
{
    public required string PresetName { get; set; }
    public required string PresetDirectory { get; set; }
    public required string ProjectDirectory { get; set; }

    /// <summary>编译后的系统注入块(含标记)。空 = 无注入。</summary>
    public string SystemBlock { get; set; } = "";

    /// <summary>写入的注入文件路径(供 --system-prompt 覆盖默认系统提示词)。</summary>
    public string? SystemBlockFile { get; set; }

    /// <summary>pi 参数(不含 pi 可执行文件本身,不含位置参数开场白)。</summary>
    public required List<string> Arguments { get; set; }

    /// <summary>开场用户消息(可为空)。</summary>
    public string OpeningMessage { get; set; } = "";

    /// <summary>环境变量。</summary>
    public required Dictionary<string, string> Environment { get; set; }

    public List<string> Warnings { get; set; } = new();

    /// <summary>用于展示的完整命令行。</summary>
    public string CommandLine => "pi " + string.Join(" ", Arguments.Select(PresetCompiler.QuoteArg))
        + (string.IsNullOrWhiteSpace(OpeningMessage) ? "" : " " + PresetCompiler.QuoteArg(OpeningMessage));
}
