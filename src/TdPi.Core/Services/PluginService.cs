using System.Text.Json.Nodes;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// 插件管理:
/// - Pi 包(settings.json packages[],npm:/git:/本地)→ 对象形式空资源列表 = 禁用
/// - extensions/ 目录中的本地扩展文件/目录 → 重命名 .disabled 后缀 = 禁用
/// - settings.json extensions[] 路径条目 → "-path" 排除前缀 = 禁用
/// </summary>
public class PluginService
{
    private readonly PiEnvironment _env;

    public PluginService(PiEnvironment env) => _env = env;

    public List<PluginInfo> ListPlugins(string? project)
    {
        var result = new List<PluginInfo>();
        LoadPackages(_env.GlobalSettingsFile, isGlobal: true, result);
        LoadExtensionsDir(_env.GlobalExtensionsDir, isGlobal: true, result);
        LoadSettingsPaths(_env.GlobalSettingsFile, isGlobal: true, result);

        if (!string.IsNullOrEmpty(project))
        {
            var ps = _env.ProjectSettingsFile(project);
            if (File.Exists(ps))
            {
                LoadPackages(ps, isGlobal: false, result);
                LoadSettingsPaths(ps, isGlobal: false, result);
            }
            var pe = _env.ProjectExtensionsDir(project);
            if (Directory.Exists(pe)) LoadExtensionsDir(pe, isGlobal: false, result);
        }
        return result
            .OrderBy(p => p.IsGlobal ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- Pi 包 ----------

    private void LoadPackages(string settingsFile, bool isGlobal, List<PluginInfo> result)
    {
        var root = JsonHelper.TryLoadNode(settingsFile) as JsonObject;
        if (root?["packages"] is not JsonArray packages) return;
        foreach (var entry in packages)
        {
            switch (entry)
            {
                case JsonValue v when v.TryGetValue<string>(out var src):
                {
                    result.Add(new PluginInfo
                    {
                        Kind = PluginKind.Package,
                        DisplayName = DisplayNameOfSource(src),
                        Source = src,
                        Enabled = true,
                        IsGlobal = isGlobal,
                        ToggleLocation = settingsFile,
                        Detail = PackageDetail(src),
                    });
                    break;
                }
                case JsonObject obj:
                {
                    var src = obj["source"]?.GetValue<string>() ?? "";
                    var resKeys = new[] { "extensions", "skills", "prompts", "themes" };
                    var specifiedEmpty = resKeys.Count(k => obj[k] is JsonArray a && a.Count == 0);
                    var specifiedAny = resKeys.Count(k => obj.ContainsKey(k));
                    var enabled = specifiedEmpty < 4; // 四类全空 = 完全禁用
                    result.Add(new PluginInfo
                    {
                        Kind = PluginKind.Package,
                        DisplayName = DisplayNameOfSource(src),
                        Source = src,
                        Enabled = enabled,
                        Partial = enabled && specifiedAny > 0,
                        IsGlobal = isGlobal,
                        ToggleLocation = settingsFile,
                        Detail = PackageDetail(src) + (enabled ? "" : " · 已禁用(空资源过滤)")
                            + (enabled && specifiedAny > 0 ? " · 部分资源被过滤" : ""),
                    });
                    break;
                }
            }
        }
    }

    private static string DisplayNameOfSource(string source)
    {
        var s = source.Trim();
        if (s.StartsWith("npm:", StringComparison.OrdinalIgnoreCase)) return s["npm:".Length..];
        if (s.StartsWith("git:", StringComparison.OrdinalIgnoreCase)) return s["git:".Length..];
        return Path.GetFileName(s.TrimEnd('/', '\\'));
    }

    private string PackageDetail(string source)
    {
        try
        {
            var s = source.Trim();
            if (s.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = s["npm:".Length..];
                string name;
                if (raw.StartsWith('@'))
                {
                    // @scope/pkg(@version) —— 取到第二个 '/' 或末尾
                    var firstSlash = raw.IndexOf('/');
                    var secondSlash = firstSlash >= 0 ? raw.IndexOf('/', firstSlash + 1) : -1;
                    name = secondSlash >= 0 ? raw[..secondSlash] : raw;
                }
                else
                {
                    var at = raw.IndexOf('@');
                    name = at > 0 ? raw[..at] : raw;
                }
                var pkgDir = Path.Combine(_env.AgentDir, "npm", "node_modules", name);
                var pkgFile = Path.Combine(pkgDir, "package.json");
                if (File.Exists(pkgFile))
                {
                    var node = JsonHelper.TryLoadNode(pkgFile) as JsonObject;
                    var ver = node?["version"]?.GetValue<string>() ?? "";
                    var pi = node?["pi"] as JsonObject;
                    var counts = new List<string>();
                    if (pi != null)
                    {
                        foreach (var (key, label) in new[] { ("extensions", "扩展"), ("skills", "技能"), ("prompts", "模板"), ("themes", "主题") })
                        {
                            if (pi[key] is JsonArray a && a.Count > 0) counts.Add($"{label}×{a.Count}");
                        }
                    }
                    var parts = new List<string>();
                    if (ver.Length > 0) parts.Add($"v{ver}");
                    if (counts.Count > 0) parts.Add(string.Join(" ", counts));
                    return string.Join(" · ", parts);
                }
                return "未安装(重启 pi 后生效)";
            }
            if (s.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            {
                var repo = s["git:".Length..];
                return "git 仓库";
            }
            return "本地路径";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>包启停:禁用 = 对象形式四类空数组;启用 = 字符串形式。条目不存在时抛异常(不静默 no-op)。</summary>
    public void TogglePackage(PluginInfo plugin)
    {
        var settingsFile = plugin.ToggleLocation;
        var root = JsonHelper.TryLoadNode(settingsFile) as JsonObject;
        if (root?["packages"] is not JsonArray packages)
            throw new InvalidOperationException($"找不到 packages 配置({settingsFile}),可能已被删除或修改 —— 请先刷新列表。");
        for (var i = 0; i < packages.Count; i++)
        {
            var entry = packages[i];
            var src = entry switch
            {
                JsonValue v => v.GetValue<string>(),
                JsonObject o => o["source"]?.GetValue<string>(),
                _ => null,
            };
            if (src == null || !src.Equals(plugin.Source, StringComparison.OrdinalIgnoreCase)) continue;

            if (plugin.Enabled)
            {
                packages[i] = new JsonObject
                {
                    ["source"] = plugin.Source,
                    ["extensions"] = new JsonArray(),
                    ["skills"] = new JsonArray(),
                    ["prompts"] = new JsonArray(),
                    ["themes"] = new JsonArray(),
                };
            }
            else
            {
                packages[i] = JsonValue.Create(plugin.Source);
            }
            JsonHelper.Save(settingsFile, root);
            plugin.Enabled = !plugin.Enabled;
            plugin.Partial = false;
            return;
        }
    }

    // ---------- extensions 目录 ----------

    private static readonly string[] EntryFiles = { "index.ts", "index.js" };

    private void LoadExtensionsDir(string dir, bool isGlobal, List<PluginInfo> result)
    {
        if (!Directory.Exists(dir)) return;
        // 单文件扩展
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var isTs = ext is ".ts" or ".js";
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var stem = isDisabled ? file[..^".disabled".Length] : file;
            var realExt = Path.GetExtension(stem).ToLowerInvariant();
            if (realExt is not (".ts" or ".js")) continue;
            result.Add(new PluginInfo
            {
                Kind = PluginKind.ExtensionEntry,
                DisplayName = Path.GetFileName(file),
                Source = file,
                Enabled = !isDisabled,
                IsGlobal = isGlobal,
                ToggleLocation = dir,
                EntryFile = file,
                Detail = isDisabled ? "已禁用(.disabled)" : "",
            });
        }
        // 目录扩展
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(".")) continue;
            var entry = EntryFiles.Select(f => Path.Combine(sub, f)).FirstOrDefault(File.Exists);
            var disabledEntry = EntryFiles.Select(f => Path.Combine(sub, f + ".disabled")).FirstOrDefault(File.Exists);
            if (entry == null && disabledEntry == null) continue;
            var active = entry ?? disabledEntry;
            result.Add(new PluginInfo
            {
                Kind = PluginKind.ExtensionEntry,
                DisplayName = name,
                Source = sub,
                Enabled = entry != null,
                IsGlobal = isGlobal,
                ToggleLocation = sub,
                EntryFile = active,
                Detail = entry != null ? Path.GetFileName(entry) : $"已禁用({Path.GetFileName(disabledEntry)})",
            });
        }
    }

    /// <summary>启停扩展(重命名入口文件)。</summary>
    public void ToggleExtension(PluginInfo plugin)
    {
        if (plugin.EntryFile == null) return;
        var target = plugin.Enabled ? plugin.EntryFile + ".disabled" : plugin.EntryFile[..^".disabled".Length];
        File.Move(plugin.EntryFile, target);
        plugin.EntryFile = target;
        plugin.Enabled = !plugin.Enabled;
    }

    // ---------- settings extensions[] 路径 ----------

    private void LoadSettingsPaths(string settingsFile, bool isGlobal, List<PluginInfo> result)
    {
        var root = JsonHelper.TryLoadNode(settingsFile) as JsonObject;
        if (root?["extensions"] is not JsonArray exts) return;
        foreach (var entry in exts)
        {
            if (entry is not JsonValue v || v.GetValue<string>() is not { } s) continue;
            var disabled = s.StartsWith("-");
            var enabledPlus = s.StartsWith("+");
            var path = disabled || enabledPlus ? s[1..] : s;
            result.Add(new PluginInfo
            {
                Kind = PluginKind.SettingsPath,
                DisplayName = Path.GetFileName(path) == path ? path : Path.GetFileName(path),
                Source = s,
                Enabled = !disabled,
                IsGlobal = isGlobal,
                ToggleLocation = settingsFile,
                EntryFile = ResolveSettingsPath(path, settingsFile),
                Detail = "settings 条目" + (disabled ? " · 已排除" : ""),
            });
        }
    }

    private static string ResolveSettingsPath(string path, string settingsFile)
    {
        try
        {
            if (Path.IsPathRooted(path)) return path;
            var baseDir = Path.GetDirectoryName(settingsFile)!;
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
        catch
        {
            return path;
        }
    }

    /// <summary>启停 settings extensions[] 条目(+/- 前缀切换)。条目不存在时抛异常(不静默 no-op)。</summary>
    public void ToggleSettingsPath(PluginInfo plugin)
    {
        var settingsFile = plugin.ToggleLocation;
        var root = JsonHelper.TryLoadNode(settingsFile) as JsonObject;
        if (root?["extensions"] is not JsonArray exts)
            throw new InvalidOperationException($"找不到 extensions 配置({settingsFile}),可能已被删除或修改 —— 请先刷新列表。");
        var path = plugin.Source.StartsWith('-') || plugin.Source.StartsWith('+')
            ? plugin.Source[1..]
            : plugin.Source;
        for (var i = 0; i < exts.Count; i++)
        {
            if (exts[i] is not JsonValue v) continue;
            var s = v.GetValue<string>();
            var bare = s.StartsWith('-') || s.StartsWith('+') ? s[1..] : s;
            if (!bare.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
            exts[i] = JsonValue.Create(plugin.Enabled ? "-" + path : "+" + path);
            JsonHelper.Save(settingsFile, root);
            plugin.Source = plugin.Enabled ? "-" + path : "+" + path;
            plugin.Enabled = !plugin.Enabled;
            return;
        }
    }
}
