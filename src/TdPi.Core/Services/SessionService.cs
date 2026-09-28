using System.Globalization;
using System.Text;
using System.Text.Json;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// 会话记录:扫描 ~/.pi/agent/sessions/&lt;项目&gt;/*.jsonl。
/// 预设识别:优先读会话里 td-pi-preset 扩展写入的 custom 记录(含预设名与来源),
/// 无扩展记录时兜底识别旧版系统提示词里的注入块标记(v1.3 及更早;新版为纯内容无标记)。
/// </summary>
public class SessionService
{
    private const string RecordType = "td-pi-preset";
    private const string BeginMarker = "td-pi-preset:begin name=";

    private readonly PiEnvironment _env;

    public SessionService(PiEnvironment env) => _env = env;

    /// <summary>列出全部会话,按最后活动时间倒序。</summary>
    public List<SessionInfo> ListSessions()
    {
        var result = new List<SessionInfo>();
        var root = _env.SessionsRoot;
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.jsonl").ToList();
            }
            catch
            {
                continue;
            }
            foreach (var file in files)
            {
                try
                {
                    var info = ParseSession(file);
                    if (info != null) result.Add(info);
                }
                catch
                {
                    // 单个会话文件损坏不影响其它
                }
            }
        }
        return result.OrderByDescending(s => s.LastModified).ToList();
    }

    /// <summary>流式解析一个会话文件(避免整文件载入内存)。</summary>
    private SessionInfo? ParseSession(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length == 0) return null;

        var info = new SessionInfo
        {
            File = file,
            LastModified = fi.LastWriteTime,
            SizeBytes = fi.Length,
        };

        var hasCustomRecord = false;
        string? blockName = null;
        var firstUserParsed = false;

        using var sr = new StreamReader(file);
        string? line;
        var lineNo = 0;
        while ((line = sr.ReadLine()) != null)
        {
            lineNo++;
            if (line.Length < 10) continue;

            if (lineNo == 1)
            {
                if (!line.Contains("\"type\":\"session\"")) return null; // 不是会话文件
                ParseHeader(line, info);
                continue;
            }

            if (line.StartsWith("{\"type\":\"message\""))
            {
                var isUser = line.Contains("\"role\":\"user\"");
                var isAssistant = !isUser && line.Contains("\"role\":\"assistant\"");
                var isSystem = !isUser && !isAssistant && line.Contains("\"role\":\"system\"");
                if (isUser || isAssistant)
                {
                    info.MessageCount++;
                }
                if (isUser && !firstUserParsed)
                {
                    firstUserParsed = true;
                    info.FirstUserMessage = TryParseMessagePreview(line);
                }
                // 兜底:旧版(v1.3-)管理器启动注入的系统提示词带注入块标记(扩展未装/未写记录时)。
                // 仅在 system 消息行中识别,避免用户/助手消息里出现同名文本造成误判。
                if (!hasCustomRecord && blockName == null && isSystem && line.Contains(BeginMarker))
                {
                    blockName = ExtractBlockName(line);
                }
                continue;
            }

            if (line.StartsWith("{\"type\":\"custom\"") && line.Contains(RecordType))
            {
                if (TryParsePresetRecord(line, out var name, out var source, out var sys, out var ctx))
                {
                    hasCustomRecord = true;
                    info.PresetName = name;
                    info.PresetSource = source;
                    info.PresetSystemMessages = sys;
                    info.PresetContextMessages = ctx;
                    info.PresetOrigin = "session";
                }
                continue;
            }

            if (line.Contains("\"session_info\""))
            {
                var name = TryGetStringProperty(line, "name");
                if (!string.IsNullOrEmpty(name)) info.Name = name!;
                continue;
            }

            if (line.Contains("\"model_change\""))
            {
                var provider = TryGetStringProperty(line, "provider") ?? "";
                var modelId = TryGetStringProperty(line, "modelId") ?? "";
                if (modelId.Length > 0)
                {
                    info.Model = provider.Length > 0 ? $"{provider}/{modelId}" : modelId;
                }
            }
        }

        // 无扩展记录但有注入块 → 管理器启动注入(argv)
        if (!hasCustomRecord && !string.IsNullOrEmpty(blockName))
        {
            info.PresetName = blockName!;
            info.PresetOrigin = "argv";
            info.PresetSource = "launch";
        }

        return info;
    }

    private void ParseHeader(string line, SessionInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                info.Id = id.GetString() ?? "";
            if (root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                info.Cwd = cwd.GetString() ?? "";
            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
            {
                info.StartedAt = t;
            }
        }
        catch
        {
            // 头行损坏:仍按空值处理
        }
    }

    private static bool TryParsePresetRecord(
        string line, out string name, out string source, out int system, out int context)
    {
        name = source = "";
        system = context = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("customType", out var ct) || ct.ValueKind != JsonValueKind.String
                || ct.GetString() != RecordType)
            {
                return false;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return false;
            if (data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString() ?? "";
            if (data.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
                source = s.GetString() ?? "";
            if (data.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Object)
            {
                if (msgs.TryGetProperty("system", out var sysEl) && sysEl.ValueKind == JsonValueKind.Number)
                    system = sysEl.GetInt32();
                if (msgs.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                    context = ctxEl.GetInt32();
            }
            return name.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 JSON 行(转义文本)提取注入块名:td-pi-preset:begin name=X --&gt;。</summary>
    private static string? ExtractBlockName(string line)
    {
        var i = line.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (i < 0) return null;
        i += BeginMarker.Length;
        var end = line.IndexOf(" -->", i, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(line.Length, i + 200);
        var name = line[i..end].Trim();
        // sanitizeName 会把 > 换成 _、-- 换成 __,这里原样展示即可
        return name.Length > 0 ? name : null;
    }

    private static string? TryGetStringProperty(string line, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    /// <summary>解析首条用户消息文本预览(字符串或 blocks 数组),压缩为单行并截断。</summary>
    private static string TryParseMessagePreview(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                return "";
            if (!msg.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                || role.GetString() != "user")
            {
                return "";
            }
            if (!msg.TryGetProperty("content", out var content)) return "";
            return Condense(ExtractText(content), 160);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从 message.content 提取文本(string 或 [{type:"text",text:...}] 数组)。</summary>
    internal static string ExtractText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(block.GetString());
                    }
                    else if (block.ValueKind == JsonValueKind.Object
                             && block.TryGetProperty("type", out var t)
                             && t.ValueKind == JsonValueKind.String
                             && t.GetString() == "text"
                             && block.TryGetProperty("text", out var txt)
                             && txt.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(txt.GetString());
                    }
                }
                return sb.ToString();
            }
            default:
                return "";
        }
    }

    private static string Condense(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= maxChars ? single : single[..maxChars] + "…";
    }

    // ---------- 对话文本导出 ----------

    /// <summary>
    /// 生成可读对话文本(查看记录用)。system 条目为 pi 框架提示词,不展开;
    /// 预设的上下文消息不写入会话文件,因此不在导出中(它们每次请求动态拼装)。
    /// </summary>
    public string BuildTranscript(SessionInfo info, int maxTotalChars = 300_000, int maxMsgChars = 8_000)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('═', 46));
        sb.AppendLine($"会话:{(string.IsNullOrWhiteSpace(info.Name) ? "(未命名)" : info.Name)}");
        if (info.Id.Length > 0) sb.AppendLine($"ID:{info.Id}");
        sb.AppendLine($"项目:{info.Cwd}");
        sb.AppendLine($"开始:{info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}    最后活动:{info.LastModified:yyyy-MM-dd HH:mm}");
        if (info.Model.Length > 0) sb.AppendLine($"模型:{info.Model}");
        sb.AppendLine(info.UsedPreset
            ? $"预设:{info.PresetName}({info.PresetSourceText})"
            : "预设:未使用(正常启动)");
        sb.AppendLine($"对话消息:{info.MessageCount} 条    大小:{FormatSize(info.SizeBytes)}");
        sb.AppendLine($"文件:{info.File}");
        sb.AppendLine(new string('═', 46));
        sb.AppendLine();

        try
        {
            using var sr = new StreamReader(info.File);
            string? line;
            var total = sb.Length;
            var truncated = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (!line.StartsWith("{\"type\":\"message\"")) continue;
                string? role = null, ts = null, text;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                        continue;
                    if (msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String)
                        role = r.GetString();
                    if (root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String)
                        ts = t.GetString();
                    if (!msg.TryGetProperty("content", out var content)) continue;
                    text = ExtractText(content);
                }
                catch
                {
                    continue;
                }
                if (role is not ("user" or "assistant")) continue; // system / 工具结果跳过
                if (string.IsNullOrWhiteSpace(text)) continue;

                var time = "";
                if (ts != null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var tt))
                    time = tt.ToLocalTime().ToString("MM-dd HH:mm:ss");
                sb.AppendLine($"[{(role == "user" ? "用户" : "AI")}] {time}");
                var body = text.Length <= maxMsgChars ? text : text[..maxMsgChars] + "\n…(本条消息过长已截断)";
                sb.AppendLine(body);
                sb.AppendLine();
                total += body.Length + 32;
                if (total > maxTotalChars)
                {
                    truncated = true;
                    break;
                }
            }
            if (truncated)
            {
                sb.AppendLine("……(内容过长,仅显示前面部分;完整内容请打开会话文件)");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(读取会话文件失败:{ex.Message})");
        }
        return sb.ToString();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):F1} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):F1} KB",
        _ => $"{bytes} B",
    };

    // ---------- 删除 ----------

    /// <summary>删除会话文件;删空的项目目录一并移除。返回(成功数, 错误列表)。</summary>
    public (int Deleted, List<string> Errors) Delete(IEnumerable<string> files)
    {
        int deleted = 0;
        var errors = new List<string>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f))
                {
                    // File.Delete 对不存在路径静默成功;这里显式计为错误,避免虚报删除数
                    errors.Add($"{Path.GetFileName(f)}:文件不存在(可能已被删除)");
                    continue;
                }
                var dir = Path.GetDirectoryName(f);
                File.Delete(f);
                deleted++;
                if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(f)}:{ex.Message}");
            }
        }
        foreach (var d in dirs)
        {
            try
            {
                if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                {
                    Directory.Delete(d);
                }
            }
            catch
            {
                // 目录删除失败不影响会话删除结果
            }
        }
        return (deleted, errors);
    }
}
