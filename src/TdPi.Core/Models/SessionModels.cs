namespace TdPi.Core.Models;

/// <summary>本地会话记录(~/.pi/agent/sessions/&lt;项目目录&gt;/*.jsonl 中的一份)。</summary>
public class SessionInfo
{
    /// <summary>jsonl 文件完整路径。</summary>
    public required string File { get; set; }

    public string Id { get; set; } = "";

    /// <summary>会话启动时的工作目录(项目)。</summary>
    public string Cwd { get; set; } = "";

    /// <summary>会话开始时间(UTC)。</summary>
    public DateTime StartedAt { get; set; }

    public DateTime LastModified { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>会话名(session_info 记录;可能为空)。</summary>
    public string Name { get; set; } = "";

    /// <summary>user + assistant 消息条数(不含 system / 工具结果)。</summary>
    public int MessageCount { get; set; }

    /// <summary>首条用户消息预览(截断,已去掉换行)。</summary>
    public string FirstUserMessage { get; set; } = "";

    /// <summary>最近的模型(provider/modelId)。</summary>
    public string Model { get; set; } = "";

    // ---------- 预设识别 ----------

    /// <summary>生效的预设名;空 = 未使用预设(正常启动)。</summary>
    public string PresetName { get; set; } = "";

    /// <summary>识别来源:session(会话记录)/ argv(旧版系统提示词注入块标记)。</summary>
    public string PresetOrigin { get; set; } = "";

    /// <summary>注入来源:session_start / command / launch(仅 argv 标记时未知)。</summary>
    public string PresetSource { get; set; } = "";

    /// <summary>预设系统消息条数(会话记录里带的话)。</summary>
    public int PresetSystemMessages { get; set; }

    /// <summary>预设上下文消息条数(会话记录里带的话)。</summary>
    public int PresetContextMessages { get; set; }

    public bool UsedPreset => !string.IsNullOrEmpty(PresetName);

    /// <summary>预设识别方式说明(徽章提示用)。</summary>
    public string PresetOriginText => PresetOrigin switch
    {
        "session" => "会话记录(td-pi-preset 扩展写入)",
        "argv" => "系统提示词注入块标记(旧版会话)",
        _ => "",
    };

    /// <summary>注入来源说明。</summary>
    public string PresetSourceText => PresetSource switch
    {
        "session_start" => "启动时生效(session_start 记录)",
        "command" => "会话内切换(旧版扩展)",
        "launch" => "管理器启动注入",
        _ => "",
    };
}
