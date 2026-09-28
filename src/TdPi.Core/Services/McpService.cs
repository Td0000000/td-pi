using System.Text.Json.Nodes;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// MCP 服务管理(基于 pi-mcp-adapter 配置体系)。
///
/// 层级(低→高):
///   1. ~/.config/mcp/mcp.json            全局共享
///   2. ~/.agents/mcp.json                全局兼容
///   3. ~/.agents/mcp/mcp.json            全局兼容
///   4. ~/.pi/agent/mcp-adapter.json      全局适配器覆盖
///   5. <proj>/.mcp.json                  项目共享
///   6. <proj>/.pi/mcp-adapter.json       项目适配器覆盖(最高)
///
/// 服务器级替换合并;disabled 字段为启停开关。
/// 启停写入适配器覆盖层(与 /mcp-adapter enable|disable 的行为一致),不改共享文件。
/// </summary>
public class McpService
{
    private readonly PiEnvironment _env;

    public McpService(PiEnvironment env) => _env = env;

    private record Layer(string File, string Scope, bool FromProject, bool AdapterOwned, int Order);

    private List<Layer> Layers(string? project)
    {
        var list = new List<Layer>
        {
            new(_env.SharedGlobalMcpFile, "全局共享 (~/.config/mcp/mcp.json)", false, false, 1),
            new(_env.AgentsMcpFile, "全局兼容 (~/.agents/mcp.json)", false, false, 2),
            new(_env.AgentsMcpDirFile, "全局兼容 (~/.agents/mcp/mcp.json)", false, false, 3),
            new(_env.GlobalMcpAdapterFile, "全局覆盖 (~/.pi/agent/mcp-adapter.json)", false, true, 4),
        };
        if (!string.IsNullOrEmpty(project))
        {
            list.Add(new Layer(_env.ProjectSharedMcpFile(project), "项目共享 (.mcp.json)", true, false, 5));
            list.Add(new Layer(_env.ProjectAdapterMcpFile(project), "项目覆盖 (.pi/mcp-adapter.json)", true, true, 6));
        }
        return list;
    }

    /// <summary>合并后的服务器视图。</summary>
    public List<McpServerInfo> ListServers(string? project)
    {
        var layers = Layers(project);
        var merged = MergeDefinitions(layers);
        var source = SourceOf(layers);

        var result = new List<McpServerInfo>();
        foreach (var (name, def) in merged)
        {
            var src = source[name];
            var disabled = def["disabled"]?.GetValue<bool>() ?? false;
            var transport = def["url"] != null ? "http"
                : def["socket"] != null ? "socket"
                : def["command"] != null ? "stdio" : "未知";
            var summary = "";
            if (def["url"] is JsonValue urlVal && urlVal.TryGetValue<string>(out var url))
            {
                summary = url;
            }
            else if (def["command"] != null)
            {
                var partsList = new List<string>();
                if (def["command"] is JsonValue cmdVal && cmdVal.TryGetValue<string>(out var cmd))
                    partsList.Add(cmd);
                if (def["args"] is JsonArray arr)
                    partsList.AddRange(arr.Select(a => a?.GetValue<string>() ?? ""));
                summary = string.Join(" ", partsList);
            }
            var directTools = "";
            if (def["directTools"] is JsonValue dtVal && dtVal.TryGetValue<bool>(out var dtb) && dtb) directTools = "全部直连";
            else if (def["directTools"] is JsonArray) directTools = "部分直连";
            result.Add(new McpServerInfo
            {
                Name = name,
                DefinitionJson = JsonHelper.Serialize(def),
                SourceFile = src.File,
                SourceScope = src.Scope,
                FromProject = src.FromProject,
                Disabled = disabled,
                Transport = transport,
                Summary = summary,
                DirectTools = directTools,
            });
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>按层序合并 mcpServers(name → 合并后的定义)。</summary>
    private Dictionary<string, JsonObject> MergeDefinitions(IEnumerable<Layer> layers)
    {
        var merged = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers.OrderBy(l => l.Order))
        {
            var node = JsonHelper.TryLoadNode(layer.File) as JsonObject;
            if (node?["mcpServers"] is not JsonObject servers) continue;
            foreach (var server in servers)
            {
                if (server.Value is not JsonObject def) continue;
                if (!merged.TryGetValue(server.Key, out var existing))
                {
                    existing = new JsonObject();
                    merged[server.Key] = existing;
                }
                JsonHelper.DeepMerge(existing, def.DeepClone());
            }
        }
        return merged;
    }

    /// <summary>每个服务器名 → 定义其最终值的层。</summary>
    private Dictionary<string, Layer> SourceOf(IEnumerable<Layer> layers)
    {
        var source = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers.OrderBy(l => l.Order))
        {
            var node = JsonHelper.TryLoadNode(layer.File) as JsonObject;
            if (node?["mcpServers"] is not JsonObject servers) continue;
            foreach (var server in servers)
            {
                if (server.Value is JsonObject) source[server.Key] = layer;
            }
        }
        return source;
    }

    public bool IsAdapterInstalled(string? project) => _env.IsMcpAdapterInstalled(project);

    // ---------- 启停 ----------

    /// <summary>
    /// 启/停一个 server:写入对应作用域的适配器覆盖文件。
    /// 项目层定义 → .pi/mcp-adapter.json;全局层定义 → ~/.pi/agent/mcp-adapter.json。
    /// </summary>
    public void SetDisabled(McpServerInfo server, bool disabled, string? project)
    {
        var target = server.FromProject && !string.IsNullOrEmpty(project)
            ? _env.ProjectAdapterMcpFile(project)
            : _env.GlobalMcpAdapterFile;

        var root = JsonHelper.TryLoadNode(target) as JsonObject ?? new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;

        if (disabled)
        {
            // 只保留 disabled 字段(与 /mcp-adapter disable 行为一致,不改写共享文件)
            servers[server.Name] = new JsonObject { ["disabled"] = true };
        }
        else
        {
            // 与 /mcp-adapter enable 行为一致:若更低层(非适配器覆盖层)的定义本身自带 disabled:true,
            // 移除覆盖条目无法启用,需写显式 disabled:false 压制;否则直接移除覆盖条目。
            var underlying = MergeDefinitions(Layers(project).Where(l => !l.AdapterOwned));
            var lowerDisabled = underlying.TryGetValue(server.Name, out var def)
                && def["disabled"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d;
            if (lowerDisabled)
                servers[server.Name] = new JsonObject { ["disabled"] = false };
            else
                servers.Remove(server.Name);
        }
        JsonHelper.Save(target, root);
    }

    // ---------- 增删改 ----------

    /// <summary>新增 server 到指定文件(global=true → ~/.config/mcp/mcp.json,否则 .mcp.json)。</summary>
    public void AddServer(string name, JsonObject definition, bool global, string project)
    {
        var target = global ? _env.SharedGlobalMcpFile : _env.ProjectSharedMcpFile(project);
        var existing = ListServers(project);
        if (existing.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"服务器 {name} 已存在。");
        WriteServer(target, name, definition);
    }

    /// <summary>编辑:把新定义写入当前定义所在层(替换语义)。</summary>
    public void UpdateServer(McpServerInfo server, JsonObject newDefinition, string? project)
    {
        WriteServer(server.SourceFile, server.Name, newDefinition);
    }

    private static void WriteServer(string file, string name, JsonObject definition)
    {
        var root = JsonHelper.TryLoadNode(file) as JsonObject ?? new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers[name] = definition.DeepClone();
        JsonHelper.Save(file, root);
    }

    /// <summary>从所有层移除 server。</summary>
    public void RemoveServer(McpServerInfo server, string? project)
    {
        foreach (var layer in Layers(project))
        {
            if (!File.Exists(layer.File)) continue;
            var root = JsonHelper.TryLoadNode(layer.File) as JsonObject;
            if (root?["mcpServers"] is not JsonObject servers) continue;
            if (servers.ContainsKey(server.Name))
            {
                servers.Remove(server.Name);
                JsonHelper.Save(layer.File, root);
            }
        }
    }

    /// <summary>确保全局共享 MCP 文件存在(带空 mcpServers 骨架);project 非空时一并确保项目文件。</summary>
    public void EnsureSharedFiles(string? project)
    {
        if (!File.Exists(_env.SharedGlobalMcpFile))
        {
            JsonHelper.Save(_env.SharedGlobalMcpFile, new JsonObject
            {
                ["mcpServers"] = new JsonObject(),
            });
        }
        if (string.IsNullOrEmpty(project)) return;
        var projFile = _env.ProjectSharedMcpFile(project);
        if (!File.Exists(projFile))
        {
            JsonHelper.Save(projFile, new JsonObject
            {
                ["mcpServers"] = new JsonObject(),
            });
        }
    }

    public static JsonObject ParseDefinition(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        });
        return node as JsonObject ?? throw new InvalidOperationException("server 定义必须是 JSON 对象。");
    }
}
