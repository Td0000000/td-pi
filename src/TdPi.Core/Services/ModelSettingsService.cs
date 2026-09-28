using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>
/// 自定义模型(models.json)管理。
///
/// 文件结构:
/// {
///   "providers": {
///     "&lt;name&gt;": {
///       "name": "展示名(可选)", "baseUrl": "...", "api": "openai-completions",
///       "apiKey": "明文 / $ENV_VAR / !command",
///       "headers": { }, "compat": { },
///       "models": [ { "id", "name", "reasoning", "input", "contextWindow",
///                     "maxTokens", "cost", "samplingParams", "api", "baseUrl", "headers" } ]
///     }
///   },
///   "modelOverrides": { … }   // 本服务不触碰,原样保留
/// }
///
/// 编辑服务商时从原对象克隆后再覆盖已知字段,未识别的扩展字段(如 oauth)不丢失。
/// </summary>
public class ModelSettingsService
{
    private readonly PiEnvironment _env;

    public ModelSettingsService(PiEnvironment env) => _env = env;

    public string ModelsFile => _env.ModelsFile;

    public bool FileExists => File.Exists(ModelsFile);

    // ---------- 读取 ----------

    public List<ProviderInfo> ListProviders()
    {
        var list = new List<ProviderInfo>();
        var root = JsonHelper.TryLoadNode(ModelsFile) as JsonObject;
        if (root?["providers"] is not JsonObject providers) return list;
        foreach (var p in providers)
        {
            if (p.Value is not JsonObject def) continue;
            var info = new ProviderInfo
            {
                Name = p.Key,
                DisplayName = Str(def["name"]),
                BaseUrl = Str(def["baseUrl"]),
                Api = Str(def["api"]),
                ApiKey = Str(def["apiKey"]),
                HeadersJson = def["headers"] is JsonObject h ? JsonHelper.Serialize(h) : null,
                CompatJson = def["compat"] is JsonObject c ? JsonHelper.Serialize(c) : null,
            };
            if (def["models"] is JsonArray models)
            {
                foreach (var m in models)
                {
                    if (m is not JsonObject mo) continue;
                    var md = ParseModelDef(mo);
                    if (md != null) info.Models.Add(md);
                }
            }
            list.Add(info);
        }
        return list;
    }

    private static CustomModelDef? ParseModelDef(JsonObject mo)
    {
        var id = Str(mo["id"]);
        if (string.IsNullOrEmpty(id)) return null;
        var def = new CustomModelDef
        {
            Id = id!,
            Name = Str(mo["name"]),
            Reasoning = mo["reasoning"]?.GetValueKind() == JsonValueKind.True,
            Api = Str(mo["api"]),
            BaseUrl = Str(mo["baseUrl"]),
            ContextWindow = Num(mo["contextWindow"]),
            MaxTokens = Num(mo["maxTokens"]),
            SamplingParams = mo["samplingParams"] as JsonObject,
            HeadersJson = mo["headers"] is JsonObject h ? JsonHelper.Serialize(h) : null,
        };
        if (mo["input"] is JsonArray input)
        {
            foreach (var v in input)
            {
                var s = v?.GetValue<string>();
                if (!string.IsNullOrEmpty(s)) def.Input.Add(s!);
            }
        }
        if (mo["cost"] is JsonObject cost)
        {
            def.CostInput = Dbl(cost["input"]);
            def.CostOutput = Dbl(cost["output"]);
            def.CostCacheRead = Dbl(cost["cacheRead"]);
            def.CostCacheWrite = Dbl(cost["cacheWrite"]);
        }
        return def;
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static long? Num(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        _ => null,
    };

    private static double? Dbl(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    // ---------- 原始文件读写 ----------

    public string? LoadRaw() => File.Exists(ModelsFile) ? File.ReadAllText(ModelsFile) : null;

    /// <summary>保存整个 models.json(先校验 JSON 合法,失败抛异常不写盘)。</summary>
    public void SaveRaw(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidOperationException("内容不能为空,且必须是 JSON 对象。");
        if (node is not JsonObject)
            throw new InvalidOperationException("models.json 顶层必须是 JSON 对象。");
        JsonHelper.Save(ModelsFile, node);
    }

    // ---------- 服务商增删改 ----------

    /// <summary>新增或更新服务商。originalName 为 null 表示新增;改名时移动原条目。
    /// 新增/改名撞已有名字时抛出异常(不覆写旧数据)。</summary>
    public void SaveProvider(ProviderEdit edit, string? originalName)
    {
        var name = edit.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("服务商名称不能为空。");

        var root = JsonHelper.TryLoadNode(ModelsFile) as JsonObject ?? new JsonObject();
        var providers = root["providers"] as JsonObject ?? new JsonObject();
        root["providers"] = providers;

        // 撞名保护:新增撞已有名字、或改名撞其它名字 → 拒绝覆写
        var isRename = originalName != null && !string.Equals(originalName, name, StringComparison.Ordinal);
        if (originalName == null && providers.ContainsKey(name))
            throw new InvalidOperationException($"服务商 {name} 已存在。请直接在列表中选中它进行编辑,或换一个名字。");
        if (isRename && providers.ContainsKey(name))
            throw new InvalidOperationException($"无法改名为 {name}:该名字已是另一个服务商。改名会覆盖它,请换一个名字。");

        // 从原对象克隆,保留 models 与未识别字段
        var obj = originalName != null && providers[originalName] is JsonObject old
            ? old.DeepClone().AsObject()
            : new JsonObject();

        SetOrRemove(obj, "name", edit.DisplayName);
        SetOrRemove(obj, "baseUrl", edit.BaseUrl);
        SetOrRemove(obj, "api", edit.Api);
        SetOrRemove(obj, "apiKey", edit.ApiKey);

        if (string.IsNullOrWhiteSpace(edit.HeadersJson))
            obj.Remove("headers");
        else
            obj["headers"] = ParseJsonObject(edit.HeadersJson, "自定义请求头");

        if (string.IsNullOrWhiteSpace(edit.CompatJson))
            obj.Remove("compat");
        else
            obj["compat"] = ParseJsonObject(edit.CompatJson, "兼容设置 compat");

        if (originalName != null && !string.Equals(originalName, name, StringComparison.Ordinal))
            providers.Remove(originalName);
        providers[name] = obj;
        JsonHelper.Save(ModelsFile, root);
    }

    public void RemoveProvider(string name)
    {
        if (!FileExists) return;
        var root = JsonHelper.TryLoadNode(ModelsFile) as JsonObject;
        if (root?["providers"] is not JsonObject providers || !providers.ContainsKey(name)) return;
        providers.Remove(name);
        JsonHelper.Save(ModelsFile, root);
    }

    // ---------- 模型增删改 ----------

    /// <summary>在指定服务商下新增或更新模型。originalId 为 null 表示新增;改 id 时替换原条目。
    /// 新增/改名撞已有 id 时抛出异常(不产生重复条目、不覆写旧数据)。</summary>
    public void SaveModel(string providerName, ModelEdit edit, string? originalId)
    {
        var id = edit.Id.Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("模型 ID 不能为空。");

        var contextWindow = ParseOptLong(edit.ContextWindow, "上下文窗口");
        var maxTokens = ParseOptLong(edit.MaxTokens, "最大输出");
        var costInput = ParseOptDouble(edit.CostInput, "输入费用");
        var costOutput = ParseOptDouble(edit.CostOutput, "输出费用");
        var costCacheRead = ParseOptDouble(edit.CostCacheRead, "缓存读费用");
        var costCacheWrite = ParseOptDouble(edit.CostCacheWrite, "缓存写费用");

        var root = JsonHelper.TryLoadNode(ModelsFile) as JsonObject ?? new JsonObject();
        var providers = root["providers"] as JsonObject ?? new JsonObject();
        var provider = providers[providerName] as JsonObject
            ?? throw new InvalidOperationException($"服务商 {providerName} 不存在。");
        var models = provider["models"] as JsonArray ?? new JsonArray();
        provider["models"] = models;

        // 撞 id 保护:新增撞已有 id、或改名撞其它 id → 拒绝(避免重复条目/覆写旧数据)
        var isRename = originalId != null && !string.Equals(originalId, id, StringComparison.Ordinal);
        bool IdExists(string target) => models.Any(m => m is JsonObject o && Str(o["id"])?.Equals(target, StringComparison.Ordinal) == true);
        if (originalId == null && IdExists(id))
            throw new InvalidOperationException($"模型 {id} 已存在于服务商 {providerName} 下。请直接编辑它,或换一个 ID。");
        if (isRename && IdExists(id))
            throw new InvalidOperationException($"无法改名为 {id}:该 ID 已被同服务商下另一个模型使用。");

        // 从原条目克隆,保留未识别的扩展字段(inputLimits / promptCache / thinkingLevelMap 等)
        JsonObject? original = null;
        if (originalId != null)
        {
            foreach (var item in models)
            {
                if (item is JsonObject o && Str(o["id"])?.Equals(originalId, StringComparison.Ordinal) == true)
                {
                    original = o;
                    break;
                }
            }
        }
        var m = original != null ? original.DeepClone().AsObject() : new JsonObject();

        m["id"] = id;
        var modelName = edit.Name.Trim();
        if (modelName.Length > 0 && !string.Equals(modelName, id, StringComparison.Ordinal))
            m["name"] = modelName;
        else
            m.Remove("name");
        if (edit.Reasoning) m["reasoning"] = true;
        else m.Remove("reasoning");

        var input = new JsonArray();
        if (edit.InputText) input.Add("text");
        if (edit.InputImage) input.Add("image");
        if (input.Count > 0) m["input"] = input;
        else m.Remove("input");

        if (contextWindow is > 0) m["contextWindow"] = contextWindow;
        else m.Remove("contextWindow");
        if (maxTokens is > 0) m["maxTokens"] = maxTokens;
        else m.Remove("maxTokens");

        if (costInput.HasValue || costOutput.HasValue || costCacheRead.HasValue || costCacheWrite.HasValue)
        {
            m["cost"] = new JsonObject
            {
                ["input"] = costInput ?? 0,
                ["output"] = costOutput ?? 0,
                ["cacheRead"] = costCacheRead ?? 0,
                ["cacheWrite"] = costCacheWrite ?? 0,
            };
        }
        else
        {
            m.Remove("cost");
        }

        SetOrRemove(m, "api", edit.Api);
        SetOrRemove(m, "baseUrl", edit.BaseUrl);

        // 采样参数:手动填写 JSON,自动拼接进模型条目;留空 = 不写该字段,用服务端默认
        if (string.IsNullOrWhiteSpace(edit.SamplingParamsJson))
            m.Remove("samplingParams");
        else
            m["samplingParams"] = ParseJsonObject(edit.SamplingParamsJson, "采样参数 samplingParams");

        if (string.IsNullOrWhiteSpace(edit.HeadersJson))
            m.Remove("headers");
        else
            m["headers"] = ParseJsonObject(edit.HeadersJson, "模型级请求头");

        // 替换原条目(保序),或追加
        var targetIndex = -1;
        var probe = originalId ?? id;
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i] is JsonObject existing
                && Str(existing["id"])?.Equals(probe, StringComparison.Ordinal) == true)
            {
                targetIndex = i;
                break;
            }
        }
        if (targetIndex >= 0) models[targetIndex] = m;
        else models.Add(m);

        JsonHelper.Save(ModelsFile, root);
    }

    public void RemoveModel(string providerName, string modelId)
    {
        if (!FileExists) return;
        var root = JsonHelper.TryLoadNode(ModelsFile) as JsonObject;
        if (root?["providers"] is not JsonObject providers) return;
        if (providers[providerName] is not JsonObject provider) return;
        if (provider["models"] is not JsonArray models) return;
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i] is JsonObject existing
                && Str(existing["id"])?.Equals(modelId, StringComparison.Ordinal) == true)
            {
                models.RemoveAt(i);
                JsonHelper.Save(ModelsFile, root);
                return;
            }
        }
    }

    // ---------- 解析辅助(表单校验与服务层共用) ----------

    private static void SetOrRemove(JsonObject obj, string key, string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) obj.Remove(key);
        else obj[key] = s;
    }

    /// <summary>解析可选数字;空串返回 null,非法抛异常(消息可直接展示)。</summary>
    public static double? ParseOptDouble(string? raw, string field)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException($"「{field}」不是有效数字:{s}");
        return v;
    }

    /// <summary>解析可选整数;空串返回 null,非法抛异常。</summary>
    public static long? ParseOptLong(string? raw, string field)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0) return null;
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException($"「{field}」不是有效整数:{s}");
        return v;
    }

    /// <summary>解析 JSON 对象文本;空串返回空对象,非法抛异常。</summary>
    public static JsonObject ParseJsonObject(string? raw, string field)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0) return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(s, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"「{field}」不是合法 JSON:{ex.Message}");
        }
        return node as JsonObject
            ?? throw new InvalidOperationException($"「{field}」必须是 JSON 对象(大括号包裹)。");
    }
}
