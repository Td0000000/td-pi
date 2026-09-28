using System.Text;
using System.Text.Json.Nodes;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// 预设 → pi 启动参数 编译器。
///
/// 系统提示词规范(与 extension/td-pi-preset/index.ts 保持字节级一致):
/// 仅由启用的系统消息「原文」按顺序拼接(消息之间空一行)——
/// 不含任何标记、标题、说明文字、节标题,系统提示词里只有用户填写的内容。
/// </summary>
public static class PresetCompiler
{
    // ---------- 系统提示词构建 ----------

    /// <summary>
    /// 构建系统提示词内容(仅 system 角色的启用消息原文,按顺序,空行分隔)。
    /// 无启用的系统消息时返回空串。与 TS 端字节级一致。
    /// </summary>
    public static string BuildSystemBlock(Preset preset)
    {
        var parts = preset.Messages
            .Where(m => m.Enabled && m.Role == "system")
            .Select(m => m.Content?.TrimEnd() ?? "")
            .Where(c => c.Length > 0)
            .ToList();
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// 上下文消息(user/assistant 角色,启用状态,按顺序)。
    /// 由 td-pi-preset 扩展的 context 事件在每次 LLM 调用前拼到真实对话之前 —— 不写入会话,
    /// 不是会话条目,因此永远不被压缩。
    /// </summary>
    public static List<PresetMessage> GetContextMessages(Preset preset) =>
        preset.Messages.Where(m => m.Enabled && m.Role is "user" or "assistant").ToList();

    /// <summary>旧签名兼容(片段文件模型已迁移到 Messages)。</summary>
    public static string BuildSystemBlock(Preset preset, string presetDir) => BuildSystemBlock(preset);

    public static string ResolveFragmentPath(string presetDir, string file)
    {
        return Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(presetDir, file));
    }

    // ---------- argv 编译 ----------

    public static CompiledLaunch Compile(
        Preset preset,
        string presetDir,
        string projectDir,
        PiEnvironment env,
        string? overrideOpeningMessage = null)
    {
        var warnings = new List<string>();
        var args = new List<string>();

        // 1) 模型:v1.4.1 起预设不再指定模型(预设统一用 pi 默认模型;Preset.Model 仅为旧文件兼容保留,不使用)

        // 2) 系统消息原文 → 编译文件 → --system-prompt(覆盖 pi 默认系统提示词;只含用户填写的内容)
        var block = BuildSystemBlock(preset);
        string? blockFile = null;
        if (block.Length > 0)
        {
            blockFile = CompiledBlockPath(env.CompiledDir, preset.Name, presetDir);
            File.WriteAllText(blockFile, block);
            args.Add("--system-prompt");
            args.Add(blockFile);
        }

        // 3) 上下文消息(user/AI)由扩展的 context 事件注入 —— 必须部署扩展
        var contextMessages = GetContextMessages(preset);
        if (contextMessages.Count > 0 && !File.Exists(env.TdPiExtensionFile))
        {
            warnings.Add($"预设含 {contextMessages.Count} 条用户/AI消息,由 td-pi-preset 扩展注入 —— 请在「诊断」页部署扩展,否则这些消息不会生效。");
        }

        // 4) 会话名
        var sessionName = string.IsNullOrWhiteSpace(preset.SessionName) ? preset.Name : preset.SessionName.Trim();
        if (sessionName.Length > 0)
        {
            args.Add("-n");
            args.Add(sessionName);
        }

        // 5) 扩展
        var exclusiveExt = preset.ExtensionsMode.Equals("exclusive", StringComparison.OrdinalIgnoreCase);
        if (exclusiveExt)
        {
            args.Add("--no-extensions");
            // 基础设施:MCP 适配器(若需要)+ td-pi 预设扩展
            if (!string.IsNullOrWhiteSpace(preset.McpConfig))
                args.AddRange(new[] { "-e", "npm:pi-mcp-adapter" });
            var extDir = env.TdPiExtensionDir;
            if (Directory.Exists(extDir))
                args.AddRange(new[] { "-e", extDir });
            foreach (var ext in preset.Extensions)
            {
                var p = Path.IsPathRooted(ext) ? ext : Path.GetFullPath(Path.Combine(presetDir, ext));
                if (!File.Exists(p) && !Directory.Exists(p))
                    warnings.Add($"扩展不存在:{ext}");
                args.AddRange(new[] { "-e", p });
            }
        }
        else
        {
            foreach (var ext in preset.Extensions)
            {
                var p = Path.IsPathRooted(ext) ? ext : Path.GetFullPath(Path.Combine(presetDir, ext));
                if (!File.Exists(p) && !Directory.Exists(p))
                    warnings.Add($"扩展不存在:{ext}");
                args.AddRange(new[] { "-e", p });
            }
        }

        // 6) 技能
        var exclusiveSkills = preset.SkillsMode.Equals("exclusive", StringComparison.OrdinalIgnoreCase);
        if (exclusiveSkills) args.Add("--no-skills");
        foreach (var skill in preset.Skills)
        {
            var p = Path.IsPathRooted(skill) ? skill : Path.GetFullPath(Path.Combine(presetDir, skill));
            if (!File.Exists(p) && !Directory.Exists(p))
                warnings.Add($"技能不存在:{skill}");
            args.AddRange(new[] { "--skill", p });
        }

        // 7) MCP 配置
        if (!string.IsNullOrWhiteSpace(preset.McpConfig))
        {
            var mcpPath = Path.IsPathRooted(preset.McpConfig)
                ? preset.McpConfig
                : Path.GetFullPath(Path.Combine(presetDir, preset.McpConfig));
            if (!File.Exists(mcpPath))
                warnings.Add($"MCP 配置文件不存在:{preset.McpConfig}");
            else if (!env.IsMcpAdapterInstalled(projectDir))
                warnings.Add("未检测到 pi-mcp-adapter 包(--mcp-config 由它注册)。请先在 MCP 页安装。");
            args.AddRange(new[] { "--mcp-config", mcpPath });
        }

        // 8) 开场消息(位置参数,有意可牺牲)
        var opening = overrideOpeningMessage ?? preset.OpeningMessage;

        var environment = new Dictionary<string, string>
        {
            ["TD_PI_PRESET"] = preset.Name,
            ["TD_PI_PRESET_DIR"] = presetDir,
        };

        return new CompiledLaunch
        {
            PresetName = preset.Name,
            PresetDirectory = presetDir,
            ProjectDirectory = projectDir,
            SystemBlock = block,
            SystemBlockFile = blockFile,
            Arguments = args,
            OpeningMessage = opening?.Trim() ?? "",
            Environment = environment,
            Warnings = warnings,
        };
    }

    public static List<string> MissingFragmentFiles(Preset preset, string presetDir) => new();

    /// <summary>预设名 → 安全文件名(编译注入块文件用)。</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// <summary>
    /// 编译系统块文件路径:预设名 + 预设目录短哈希。
    /// 不同项目的同名预设不再碰撞(全局 compiled 目录共享,避免并发启动时互相覆写)。
    /// </summary>
    public static string CompiledBlockPath(string compiledDir, string presetName, string presetDir)
    {
        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(presetDir));
        var hash = Convert.ToHexString(hashBytes)[..8];
        return Path.Combine(compiledDir, $"{SanitizeFileName(presetName)}-{hash}.md");
    }

    /// <summary>Windows 命令行参数加引号。</summary>
    public static string QuoteArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (!arg.Contains(' ') && !arg.Contains('"') && !arg.Contains('^') && !arg.Contains('&')) return arg;
        return "\"" + arg.Replace("\"", "'") + "\"";
    }
}
