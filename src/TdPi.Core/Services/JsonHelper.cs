using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TdPi.Core.Services;

/// <summary>容错 JSON(JSONC)读写工具。MCP 配置允许注释与尾逗号。</summary>
public static class JsonHelper
{
    public static readonly JsonSerializerOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 保留中文/+/等字符原样写出(默认 encoder 会转义成 \uXXXX,人不可读)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>加载 JSONC 文档;文件不存在返回 null;失败抛出异常。</summary>
    public static JsonDocument LoadDocument(string file)
    {
        var text = File.ReadAllText(file);
        return JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
    }

    /// <summary>加载 JSONC 节点;文件不存在或为空返回 null。</summary>
    public static JsonNode? TryLoadNode(string file)
    {
        if (!File.Exists(file)) return null;
        var text = File.ReadAllText(file).Trim();
        if (text.Length == 0) return null;
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(WriteOptions);

    /// <summary>写 JSON 文件(保持缩进与 UTF-8 无 BOM)。</summary>
    public static void Save(string file, JsonNode node)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, node.ToJsonString(WriteOptions));
    }

    /// <summary>深合并 src 到 dest(src 覆盖 dest;对象递归,其它替换)。</summary>
    public static JsonNode? DeepMerge(JsonNode? dest, JsonNode? src)
    {
        if (src is null) return dest;
        if (dest is null) return src.DeepClone();
        if (dest is JsonObject destObj && src is JsonObject srcObj)
        {
            foreach (var kv in srcObj)
            {
                destObj[kv.Key] = DeepMerge(destObj[kv.Key], kv.Value);
            }
            return destObj;
        }
        return src.DeepClone();
    }
}
