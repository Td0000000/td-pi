namespace TdPi.Core.Models;

public enum SkillScope
{
    Global,
    Project,
}

public class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public bool DisableModelInvocation { get; set; }

    /// <summary>SKILL.md 的实际路径(或禁用时的 SKILL.md.disabled)。</summary>
    public required string SkillFile { get; set; }

    /// <summary>是否目录技能(SKILL.md 位于自己的子目录中)。根目录裸 md 文件为 false。</summary>
    public bool IsDirSkill { get; set; }

    /// <summary>技能根目录(含 SKILL.md 的文件夹;独立 md 技能为其所在目录)。</summary>
    public required string Directory { get; set; }

    public SkillScope Scope { get; set; }

    /// <summary>发现来源:.pi/skills、.agents/skills 或全局 skills。</summary>
    public string SourceRoot { get; set; } = "";

    /// <summary>frontmatter 原始键值(展示用)。</summary>
    public Dictionary<string, string> ExtraFrontmatter { get; set; } = new();

    public bool IsValid { get; set; } = true;

    public string InvalidReason { get; set; } = "";
}

public class McpServerInfo
{
    public string Name { get; set; } = "";

    /// <summary>生效(逐层合并)的 server 定义 JSON 文本。</summary>
    public string DefinitionJson { get; set; } = "";

    /// <summary>主要定义所在文件(最高优先级层)。</summary>
    public required string SourceFile { get; set; }

    /// <summary>来源范围描述(全局共享/全局覆盖/项目共享/项目覆盖)。</summary>
    public required string SourceScope { get; set; }

    /// <summary>是否项目层定义。</summary>
    public bool FromProject { get; set; }

    public bool Disabled { get; set; }

    /// <summary>stdio / http / socket / 未知。</summary>
    public string Transport { get; set; } = "";

    /// <summary>摘要:命令行或 URL。</summary>
    public string Summary { get; set; } = "";

    /// <summary>directTools 摘要(如果有)。</summary>
    public string DirectTools { get; set; } = "";
}

public enum PluginKind
{
    /// <summary>settings.json packages[] 中的 Pi 包(npm:/git:/本地)。</summary>
    Package,

    /// <summary>extensions 目录中的扩展文件/目录。</summary>
    ExtensionEntry,

    /// <summary>settings.json extensions[] 中的路径条目。</summary>
    SettingsPath,
}

public class PluginInfo
{
    public PluginKind Kind { get; set; }

    /// <summary>包名 / 文件名 / 路径。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>原始 source(npm:xxx / git:xxx / 路径)。</summary>
    public string Source { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>partial:包资源被部分过滤(对象形式但未全部禁用)。</summary>
    public bool Partial { get; set; }

    public bool IsGlobal { get; set; }

    /// <summary>启停开关所在的 settings 文件 / 磁盘位置。</summary>
    public string ToggleLocation { get; set; } = "";

    /// <summary>展示详情(版本、入口文件、资源统计)。</summary>
    public string Detail { get; set; } = "";

    /// <summary>扩展实际文件路径(ExtensionEntry 用,用于重命名/定位)。</summary>
    public string? EntryFile { get; set; }
}

/// <summary>模型条目(来自 models.json)。</summary>
public class ModelInfo
{
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Reasoning { get; set; }
}
