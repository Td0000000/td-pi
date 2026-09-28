using System.Diagnostics;
using System.Text.Json;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>pi 环境探测:可执行文件、agent 目录、会话、模型表等。</summary>
public class PiEnvironment
{
    public string Home { get; }
    public string AgentDir { get; }
    public string AppDataDir { get; }

    // 关键路径
    public string GlobalSettingsFile => Path.Combine(AgentDir, "settings.json");
    public string ModelsFile => Path.Combine(AgentDir, "models.json");
    public string GlobalSkillsDir => Path.Combine(AgentDir, "skills");
    public string GlobalExtensionsDir => Path.Combine(AgentDir, "extensions");
    public string GlobalMcpAdapterFile => Path.Combine(AgentDir, "mcp-adapter.json");
    public string SharedGlobalMcpFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mcp", "mcp.json");
    public string AgentsMcpFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "mcp.json");
    public string AgentsMcpDirFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "mcp", "mcp.json");
    public string SessionsRoot => Path.Combine(AgentDir, "sessions");
    public string TdPiExtensionDir => Path.Combine(AgentDir, "extensions", "td-pi-preset");
    public string TdPiExtensionFile => Path.Combine(TdPiExtensionDir, "index.ts");

    public string CompiledDir => Path.Combine(AppDataDir, "compiled");
    public string LaunchDir => Path.Combine(AppDataDir, "launch");
    public string AppSettingsFile => Path.Combine(AppDataDir, "settings.json");

    public PiEnvironment()
    {
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        AgentDir = string.IsNullOrWhiteSpace(agentDir)
            ? Path.Combine(Home, ".pi", "agent")
            : agentDir;
        AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "td-pi");
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(CompiledDir);
        Directory.CreateDirectory(LaunchDir);
    }

    public string ProjectDir(string project) => project;

    public string ProjectSettingsFile(string project) => Path.Combine(project, ".pi", "settings.json");
    public string ProjectSkillsDir(string project) => Path.Combine(project, ".pi", "skills");
    public string ProjectAgentsSkillsDir(string project) => Path.Combine(project, ".agents", "skills");
    public string ProjectExtensionsDir(string project) => Path.Combine(project, ".pi", "extensions");
    public string ProjectSharedMcpFile(string project) => Path.Combine(project, ".mcp.json");
    public string ProjectAdapterMcpFile(string project) => Path.Combine(project, ".pi", "mcp-adapter.json");
    public string ProjectPresetsRoot(string project) => Path.Combine(project, ".pi", "td-pi", "presets");

    // ---------- 可执行文件探测 ----------

    private static readonly string[] PiCandidates =
    {
        "pi.cmd", "pi.exe", "pi.bat", "pi",
    };

    public string? PiCommand { get; private set; }
    public string? WindowsTerminal { get; private set; }
    public string? NodePath { get; private set; }

    public void Detect()
    {
        PiCommand = FindOnPath(PiCandidates)
            ?? TryFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pi.cmd"))
            ?? TryFile(Path.Combine(Home, "AppData", "Roaming", "npm", "pi.cmd"));
        WindowsTerminal = FindOnPath(new[] { "wt.exe" });
        NodePath = FindOnPath(new[] { "node.exe" });
    }

    private static string? FindOnPath(string[] names)
    {
        try
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in names)
            {
                foreach (var dir in pathDirs)
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string? TryFile(string p) => File.Exists(p) ? p : null;

    public string? GetPiVersion()
    {
        if (PiCommand == null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PiCommand,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    // ---------- 会话 / 项目 ----------

    public List<(string Path, DateTime LastUsed)> GetKnownProjects()
    {
        var result = new List<(string, DateTime)>();
        if (!Directory.Exists(SessionsRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(SessionsRoot))
        {
            try
            {
                var jsonl = Directory.EnumerateFiles(dir, "*.jsonl").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (jsonl == null) continue;
                using var sr = new StreamReader(jsonl);
                var firstLine = sr.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine)) continue;
                using var doc = JsonDocument.Parse(firstLine);
                if (doc.RootElement.TryGetProperty("cwd", out var cwdEl)
                    && cwdEl.ValueKind == JsonValueKind.String)
                {
                    var cwd = cwdEl.GetString();
                    if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
                        result.Add((cwd!, File.GetLastWriteTime(jsonl)));
                }
            }
            catch
            {
                // 单个会话目录损坏不影响其它
            }
        }
        return result.OrderByDescending(r => r.Item2).ToList();
    }

    // ---------- 模型表 ----------

    public List<ModelInfo> GetModels()
    {
        var list = new List<ModelInfo>();
        var modelsFile = Path.Combine(AgentDir, "models.json");
        if (!File.Exists(modelsFile)) return list;
        try
        {
            using var doc = JsonHelper.LoadDocument(modelsFile);
            if (!doc.RootElement.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Object) return list;
            foreach (var provider in providers.EnumerateObject())
            {
                if (!provider.Value.TryGetProperty("models", out var models)
                    || models.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in models.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var reasoning = m.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.True;
                    list.Add(new ModelInfo
                    {
                        Provider = provider.Name,
                        ModelId = id!,
                        DisplayName = string.IsNullOrEmpty(name) ? id! : name!,
                        Reasoning = reasoning,
                    });
                }
            }
        }
        catch
        {
            // 模型表损坏时返回空列表
        }
        return list;
    }

    /// <summary>检查 pi-mcp-adapter 是否已作为包安装(全局或项目)。</summary>
    public bool IsMcpAdapterInstalled(string? project = null)
    {
        foreach (var file in SettingsFiles(project))
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonHelper.LoadDocument(file);
                if (doc.RootElement.TryGetProperty("packages", out var pkgs)
                    && pkgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pkg in pkgs.EnumerateArray())
                    {
                        var src = pkg.ValueKind == JsonValueKind.String
                            ? pkg.GetString()
                            : pkg.TryGetProperty("source", out var s) ? s.GetString() : null;
                        if (src != null && src.Contains("pi-mcp-adapter", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        // 也可能是 -e 方式加载
        return false;
    }

    public IEnumerable<string> SettingsFiles(string? project)
    {
        yield return GlobalSettingsFile;
        if (!string.IsNullOrEmpty(project))
            yield return ProjectSettingsFile(project);
    }
}
