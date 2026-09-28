using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>preset.json 序列化(PresetFragment 兼容设计文档的纯字符串数组形式)。</summary>
public class PresetFragmentConverter : JsonConverter<PresetFragment>
{
    public override PresetFragment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new PresetFragment { File = reader.GetString() ?? "" };
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var node = JsonNode.Parse(ref reader);
            var f = new PresetFragment();
            if (node is JsonObject obj)
            {
                f.File = obj["file"]?.GetValue<string>() ?? "";
                f.Label = obj["label"]?.GetValue<string>() ?? "";
                f.Enabled = obj["enabled"]?.GetValue<bool>() ?? true;
            }
            return f;
        }
        throw new JsonException($"无法解析预设片段:{reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, PresetFragment value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("file", value.File);
        writer.WriteString("label", value.Label);
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteEndObject();
    }
}

/// <summary>
/// 预设的加载/保存/枚举/增删改。
/// 预设只存在于项目中:&lt;project&gt;/.pi/td-pi/presets/&lt;name&gt;/preset.json
/// </summary>
public class PresetService
{
    private static readonly JsonSerializerOptions PresetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new PresetFragmentConverter() },
    };

    private readonly PiEnvironment _env;

    public PresetService(PiEnvironment env)
    {
        _env = env;
    }

    public string PresetsRoot(string project) => _env.ProjectPresetsRoot(project);

    /// <summary>列出项目的全部预设。</summary>
    public List<PresetInfo> ListPresets(string project)
    {
        return string.IsNullOrEmpty(project)
            ? new List<PresetInfo>()
            : LoadFromRoot(PresetsRoot(project));
    }

    private List<PresetInfo> LoadFromRoot(string root)
    {
        var list = new List<PresetInfo>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var presetFile = Path.Combine(dir, "preset.json");
            if (!File.Exists(presetFile)) continue;
            try
            {
                var preset = LoadPresetFile(presetFile);
                if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = Path.GetFileName(dir);
                list.Add(new PresetInfo
                {
                    Preset = preset,
                    Directory = dir,
                });
            }
            catch
            {
                // 解析失败的预设跳过(不阻塞其它)
            }
        }
        return list.OrderBy(p => p.Preset.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Preset LoadPresetFile(string presetFile)
    {
        var json = File.ReadAllText(presetFile);
        var preset = JsonSerializer.Deserialize<Preset>(json, PresetJsonOptions)
                     ?? new Preset();
        MigrateLegacyFragments(preset, Path.GetDirectoryName(presetFile) ?? ".");
        return preset;
    }

    /// <summary>
    /// 旧格式(system/assistantExamples/sharedFragments 文件引用)→ 新格式(messages 内联)。
    /// 仅当 Messages 为空且存在旧字段时执行;片段文件内容读入 Content,保留 Enabled 状态。
    /// </summary>
    private static void MigrateLegacyFragments(Preset preset, string presetDir)
    {
        if (preset.Messages.Count == 0 &&
            (preset.System.Count > 0 || preset.SharedFragments.Count > 0 || preset.AssistantExamples.Count > 0))
        {
            foreach (var f in preset.System)
            {
                preset.Messages.Add(new PresetMessage
                {
                    Role = "system",
                    Label = string.IsNullOrWhiteSpace(f.Label)
                        ? Path.GetFileNameWithoutExtension(f.File)
                        : f.Label,
                    Content = ReadFragmentContent(presetDir, f.File),
                    Enabled = f.Enabled,
                });
            }
            foreach (var f in preset.SharedFragments)
            {
                preset.Messages.Add(new PresetMessage
                {
                    Role = "system",
                    Label = string.IsNullOrWhiteSpace(f.Label)
                        ? Path.GetFileNameWithoutExtension(f.File)
                        : f.Label,
                    Content = ReadFragmentContent(presetDir, f.File),
                    Enabled = f.Enabled,
                });
            }
            foreach (var f in preset.AssistantExamples)
            {
                preset.Messages.Add(new PresetMessage
                {
                    Role = "assistant",
                    Label = string.IsNullOrWhiteSpace(f.Label)
                        ? Path.GetFileNameWithoutExtension(f.File)
                        : f.Label,
                    Content = ReadFragmentContent(presetDir, f.File),
                    Enabled = f.Enabled,
                });
            }
        }
    }

    private static string ReadFragmentContent(string presetDir, string file)
    {
        try
        {
            var path = PresetCompiler.ResolveFragmentPath(presetDir, file);
            return File.Exists(path) ? File.ReadAllText(path).TrimEnd() : "";
        }
        catch
        {
            return "";
        }
    }

    public PresetInfo? Find(string name, string project)
    {
        return ListPresets(project).FirstOrDefault(
            p => p.Preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读取预设的片段内容。</summary>
    public string? ReadFragment(PresetInfo info, string file)
    {
        var path = PresetCompiler.ResolveFragmentPath(info.Directory, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteFragment(PresetInfo info, string file, string content)
    {
        var path = PresetCompiler.ResolveFragmentPath(info.Directory, file);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    public void Save(PresetInfo info)
    {
        // 新格式为规范存储:迁移完成后清除旧字段,避免双写漂移
        info.Preset.System = new List<PresetFragment>();
        info.Preset.AssistantExamples = new List<PresetFragment>();
        info.Preset.SharedFragments = new List<PresetFragment>();
        var presetFile = Path.Combine(info.Directory, "preset.json");
        Directory.CreateDirectory(info.Directory);
        var json = JsonSerializer.Serialize(info.Preset, PresetJsonOptions);
        File.WriteAllText(presetFile, json);
    }

    public PresetInfo Create(string name, string project, string description = "")
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidOperationException("未选择项目目录,无法创建预设(预设只存在于项目中)。");
        var root = PresetsRoot(project);
        var dir = Path.Combine(root, SafeDirName(name));
        if (Directory.Exists(dir)) throw new InvalidOperationException($"预设 {name} 已存在。");
        Directory.CreateDirectory(dir);

        var preset = new Preset
        {
            Name = name,
            Description = description,
            // 不预填任何消息:系统提示词/用户/AI 消息全部由创建者在「消息列表」页手动添加
            Messages = new List<PresetMessage>(),
        };
        var info = new PresetInfo { Preset = preset, Directory = dir };
        Save(info);
        return info;
    }

    public void Delete(PresetInfo info)
    {
        if (Directory.Exists(info.Directory))
            Directory.Delete(info.Directory, recursive: true);
    }

    /// <summary>
    /// 项目移除后清理遗留:删除项目的 .pi/td-pi 整个文件夹(预设 + 管理器数据),
    /// 空的 .pi 目录一并移除。返回 null 表示成功,否则为错误信息。
    /// </summary>
    public string? DeleteProjectPresets(string project)
    {
        try
        {
            var presetsRoot = _env.ProjectPresetsRoot(project);
            var tdPiDir = Path.GetDirectoryName(presetsRoot);
            var piDir = tdPiDir != null ? Path.GetDirectoryName(tdPiDir) : null;
            if (tdPiDir != null && Directory.Exists(tdPiDir)) Directory.Delete(tdPiDir, recursive: true);
            if (piDir != null && Directory.Exists(piDir) && !Directory.EnumerateFileSystemEntries(piDir).Any())
                Directory.Delete(piDir);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public PresetInfo Duplicate(PresetInfo info, string newName)
    {
        var root = Path.GetDirectoryName(info.Directory)!;
        var target = Path.Combine(root, SafeDirName(newName));
        if (Directory.Exists(target)) throw new InvalidOperationException($"预设 {newName} 已存在。");
        CopyDirectory(info.Directory, target);

        var preset = LoadPresetFile(Path.Combine(target, "preset.json"));
        preset.Name = newName;
        var copy = new PresetInfo { Preset = preset, Directory = target };
        Save(copy);
        return copy;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    public static string SafeDirName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        if (safe.Length == 0) throw new InvalidOperationException("预设名不能为空。");
        return safe;
    }
}
