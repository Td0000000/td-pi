using System.Text.Json;
using System.Text.Json.Serialization;

namespace TdPi.Core.Services;

/// <summary>管理器自身的设置(%LOCALAPPDATA%\td-pi\settings.json)。</summary>
public class AppSettings
{
    [JsonPropertyName("recentProjects")]
    public List<string> RecentProjects { get; set; } = new();

    [JsonPropertyName("lastProject")]
    public string LastProject { get; set; } = "";

    [JsonPropertyName("useWindowsTerminal")]
    public bool UseWindowsTerminal { get; set; } = true;

    [JsonIgnore]
    public string File { get; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppSettings(string file)
    {
        File = file;
        if (System.IO.File.Exists(file))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(file), Options);
                if (loaded != null)
                {
                    RecentProjects = loaded.RecentProjects;
                    LastProject = loaded.LastProject;
                    UseWindowsTerminal = loaded.UseWindowsTerminal;
                }
            }
            catch
            {
                // 损坏则用默认值
            }
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(File);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Options));
    }

    public void TouchProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        RecentProjects.RemoveAll(p => p.Equals(project, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, project);
        if (RecentProjects.Count > 15) RecentProjects.RemoveRange(15, RecentProjects.Count - 15);
        LastProject = project;
        Save();
    }

    /// <summary>重写项目历史(历史管理对话框用)。当前项目若被移除,LastProject 一并清空。</summary>
    public void SetRecentProjects(IReadOnlyList<string> projects, string? currentProject)
    {
        RecentProjects = projects.ToList();
        if (!string.IsNullOrEmpty(currentProject) &&
            !RecentProjects.Contains(currentProject, StringComparer.OrdinalIgnoreCase))
        {
            LastProject = "";
        }
        Save();
    }
}
