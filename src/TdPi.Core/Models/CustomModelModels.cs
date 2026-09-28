using System.Text.Json.Nodes;

namespace TdPi.Core.Models;

/// <summary>models.json 中的一个服务商(URL + Key + 模型列表)。</summary>
public class ProviderInfo
{
    /// <summary>providers 对象里的键(唯一标识)。</summary>
    public string Name { get; set; } = "";

    /// <summary>可选展示名(provider.name)。</summary>
    public string? DisplayName { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>API 协议(openai-completions / anthropic-messages / …)。</summary>
    public string? Api { get; set; }

    /// <summary>明文 / $ENV_VAR / !command。</summary>
    public string? ApiKey { get; set; }

    /// <summary>provider 级自定义请求头(JSON 文本,可空)。</summary>
    public string? HeadersJson { get; set; }

    /// <summary>provider 级兼容设置 compat(JSON 文本,可空)。</summary>
    public string? CompatJson { get; set; }

    public List<CustomModelDef> Models { get; } = new();
}

/// <summary>models.json 中的一个模型条目。</summary>
public class CustomModelDef
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool Reasoning { get; set; }

    /// <summary>输入类型(text / image)。</summary>
    public List<string> Input { get; } = new();

    public long? ContextWindow { get; set; }
    public long? MaxTokens { get; set; }
    public double? CostInput { get; set; }
    public double? CostOutput { get; set; }
    public double? CostCacheRead { get; set; }
    public double? CostCacheWrite { get; set; }

    /// <summary>覆盖 provider 默认 API 类型。</summary>
    public string? Api { get; set; }

    /// <summary>覆盖 provider 默认 baseUrl。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>默认采样参数(temperature、top_p …)。</summary>
    public JsonObject? SamplingParams { get; set; }

    /// <summary>模型级自定义请求头(JSON 文本,可空)。</summary>
    public string? HeadersJson { get; set; }
}

/// <summary>服务商表单结果(字段均为原始输入文本)。</summary>
public sealed record ProviderEdit(
    string Name,
    string DisplayName,
    string BaseUrl,
    string Api,
    string ApiKey,
    string HeadersJson,
    string CompatJson);

/// <summary>模型表单结果(数字字段为原始输入文本,由服务层解析校验)。</summary>
public sealed record ModelEdit(
    string Id,
    string Name,
    bool Reasoning,
    bool InputText,
    bool InputImage,
    string ContextWindow,
    string MaxTokens,
    string CostInput,
    string CostOutput,
    string CostCacheRead,
    string CostCacheWrite,
    string Api,
    string BaseUrl,

    // 采样参数 samplingParams —— 手动填写的 JSON 对象,自动拼接进模型条目;
    // 留空 = 不写该字段,用服务端/pi 默认。不同模型参数不同,不做固定命名输入。
    string SamplingParamsJson,
    string HeadersJson);
