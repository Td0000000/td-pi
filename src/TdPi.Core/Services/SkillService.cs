using System.Text;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// 技能管理:全局(~/.pi/agent/skills)与项目(.pi/skills、.agents/skills)。
/// 启停通过重命名 SKILL.md ↔ SKILL.md.disabled 实现(目录不再被发现 = 禁用,可视化且无损)。
/// </summary>
public class SkillService
{
    private readonly PiEnvironment _env;

    public SkillService(PiEnvironment env) => _env = env;

    public List<SkillInfo> ListSkills(string? project)
    {
        var result = new List<SkillInfo>();
        ScanRoot(_env.GlobalSkillsDir, SkillScope.Global, "全局 skills", result);
        if (!string.IsNullOrEmpty(project))
        {
            ScanRoot(_env.ProjectSkillsDir(project), SkillScope.Project, ".pi/skills", result);
            ScanRoot(_env.ProjectAgentsSkillsDir(project), SkillScope.Project, ".agents/skills", result);
        }
        return result
            .OrderBy(s => s.Scope)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanRoot(string root, SkillScope scope, string sourceRoot, List<SkillInfo> result)
    {
        if (!Directory.Exists(root)) return;
        ScanDir(root, 0);
        // 独立 md 技能(根目录下的裸 .md)
        foreach (var file in Directory.EnumerateFiles(root, "*.md"))
            result.Add(ParseSkill(file, scope, sourceRoot, isDirSkill: false));
        foreach (var file in Directory.EnumerateFiles(root, "*.md.disabled"))
            result.Add(ParseSkill(file, scope, sourceRoot, isDirSkill: false));

        void ScanDir(string dir, int depth)
        {
            if (depth > 8) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".")) continue;
                var skillMd = Path.Combine(sub, "SKILL.md");
                var skillDisabled = Path.Combine(sub, "SKILL.md.disabled");
                if (File.Exists(skillMd) || File.Exists(skillDisabled))
                {
                    result.Add(ParseSkill(File.Exists(skillMd) ? skillMd : skillDisabled,
                        scope, sourceRoot, isDirSkill: true));
                }
                else
                {
                    ScanDir(sub, depth + 1); // 未命中则继续下探
                }
            }
        }
    }

    private static SkillInfo ParseSkill(string skillFile, SkillScope scope, string sourceRoot, bool isDirSkill)
    {
        var info = new SkillInfo
        {
            SkillFile = skillFile,
            Directory = isDirSkill ? Path.GetDirectoryName(skillFile)! : Path.GetDirectoryName(skillFile)!,
            Scope = scope,
            SourceRoot = sourceRoot,
            Enabled = !skillFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
            IsDirSkill = isDirSkill,
        };
        try
        {
            var text = File.ReadAllText(skillFile);
            var fm = ParseFrontmatter(text);
            info.Name = fm.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : Path.GetFileName(isDirSkill ? Path.GetDirectoryName(skillFile)! : skillFile);
            if (fm.TryGetValue("description", out var desc)) info.Description = desc;
            info.DisableModelInvocation =
                fm.TryGetValue("disable-model-invocation", out var dmi) && dmi.Equals("true", StringComparison.OrdinalIgnoreCase);
            info.IsValid = !string.IsNullOrWhiteSpace(info.Name) && info.Description.Length > 0;
            if (!info.IsValid) info.InvalidReason = info.Description.Length == 0 ? "缺少 description(该技能不会被 pi 加载)" : "";
            foreach (var kv in fm)
            {
                if (kv.Key is not ("name" or "description" or "disable-model-invocation"))
                    info.ExtraFrontmatter[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            info.IsValid = false;
            info.InvalidReason = $"读取失败:{ex.Message}";
            info.Name = Path.GetFileName(isDirSkill ? Path.GetDirectoryName(skillFile)! : skillFile);
        }
        return info;
    }

    /// <summary>简单 YAML frontmatter 解析(仅支持 name/description 等单行键值)。</summary>
    public static Dictionary<string, string> ParseFrontmatter(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return result;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") break;
            var line = lines[i];
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0 && !result.ContainsKey(key)) result[key] = value;
        }
        return result;
    }

    public void Toggle(SkillInfo skill)
    {
        var target = skill.Enabled
            ? skill.SkillFile + ".disabled"
            : skill.SkillFile[..^".disabled".Length];
        File.Move(skill.SkillFile, target);
        skill.SkillFile = target;
        skill.Enabled = !skill.Enabled;
    }

    public void RenameSkillFile(string skillFile, string newContent)
    {
        File.WriteAllText(skillFile, newContent, new UTF8Encoding(false));
    }

    public SkillInfo Create(string root, string name, string description)
    {
        var dirName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        if (dirName.Length == 0) throw new InvalidOperationException("技能名不能为空。");
        var dir = Path.Combine(root, dirName);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"技能 {name} 已存在。");
        Directory.CreateDirectory(dir);
        var safeName = name.ToLowerInvariant().Replace(' ', '-');
        var content = $@"---
name: {safeName}
description: {description}
---

# {name}

在这里编写技能指令。
";
        var file = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return ParseSkill(file, Directory.Exists(dir) && root.StartsWith(_env.GlobalSkillsDir)
            ? SkillScope.Global : SkillScope.Project, Path.GetFileName(root), isDirSkill: true);
    }

    public void Delete(SkillInfo skill)
    {
        if (skill.IsDirSkill)
        {
            // 目录技能:整目录删除。防御:目录必须确实包含该入口文件,且不得是扫描根本身
            var dir = skill.Directory;
            if (!Directory.Exists(dir)) return;
            var entry = File.Exists(skill.SkillFile) || File.Exists(skill.SkillFile + ".disabled");
            if (!entry)
                throw new InvalidOperationException($"技能入口文件异常,拒绝删除目录:{skill.SkillFile}");
            Directory.Delete(dir, recursive: true);
        }
        else
        {
            // 独立 md 文件(含根目录裸 SKILL.md):只删文件本身,绝不删目录
            if (!File.Exists(skill.SkillFile)) return;
            File.Delete(skill.SkillFile);
            var dir = Path.GetDirectoryName(skill.SkillFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }
}
