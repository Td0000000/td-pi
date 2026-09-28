using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App;

/// <summary>通用对话框:单行输入 / 多行文本编辑 / 确认 / 信息 / 表单。</summary>
public static class Dialogs
{
    public static Window? Owner { get; set; }

    /// <summary>多行文本编辑器。返回 null 表示取消。</summary>
    public static async Task<string?> EditTextAsync(string title, string content, string hint = "", bool mono = true)
    {
        var tb = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = mono
                ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI")
                : new FontFamily("Microsoft YaHei UI"),
            MinHeight = 300,
            Height = 420,
            Width = 680,
            Watermark = hint,
        };
        var result = await ShowAsync(title, tb, "保存");
        return result ? tb.Text : null;
    }

    /// <summary>单行输入。返回 null 表示取消。</summary>
    public static async Task<string?> InputAsync(string title, string placeholder = "", string initial = "")
    {
        var tb = new TextBox { Text = initial, Watermark = placeholder, MinWidth = 400 };
        var result = await ShowAsync(title, tb, "确定");
        return result ? tb.Text : null;
    }

    /// <summary>只读文本查看器(大文本,等宽字体,可滚动)。</summary>
    public static Task ViewTextAsync(string title, string content)
    {
        var tb = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12.5,
            MinHeight = 420,
            Height = 520,
            Width = 730,
        };
        return ShowAsync(title, tb, "关闭", null);
    }

    /// <summary>确认框。</summary>
    public static Task<bool> ConfirmAsync(string title, string message) =>
        ShowBoolAsync(title, message, "确定", "取消");

    /// <summary>信息提示。</summary>
    public static Task<bool> InfoAsync(string title, string message) =>
        ShowBoolAsync(title, message, "知道了", null);

    // ---------- 表单对话框(范围在「基本」区直接选择,不再单独弹框询问) ----------

    /// <summary>系统文件夹选择器(用于选择项目文件夹)。</summary>
    public static async Task<string?> PickFolderAsync(string title)
    {
        if (Owner == null) return null;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>新建技能表单:「基本」区含 位置(全局/项目)+ 项目文件夹 + 名称 + 描述。</summary>
    public static Task<(string Name, string Desc, string Project)?> CreateSkillAsync(string? currentProject)
    {
        var name = new TextBox { Watermark = "技能名:英文小写+连字符,如 pdf-tools", MinWidth = 440 };
        var desc = new TextBox { Watermark = "一句话描述(决定模型何时会加载它)" };
        var (scope, getProject) = ScopeSection(currentProject, defaultGlobal: string.IsNullOrWhiteSpace(currentProject),
            "所有项目可用 · ~/.pi/agent/skills",
            "指定项目文件夹,技能放在它的 .pi/skills");
        var err = ErrText();
        var content = MkStack();
        content.Children.Add(Section("基本"));
        content.Children.Add(Field("位置", scope));
        content.Children.Add(Field("名称", name));
        content.Children.Add(Field("描述", desc));
        content.Children.Add(err);
        return ShowFormAsync<(string, string, string)?>("新建技能", content, () =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length == 0) { Fail(err); return null; }
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Fail(err, "技能名包含系统不允许的字符。"); return null; }
            var proj = getProject();
            if (InvalidProjectPath(proj, err)) return null;
            err.IsVisible = false;
            return ((string, string, string)?)(n, desc.Text?.Trim() ?? "", proj);
        });
    }

    /// <summary>新增 MCP 服务器表单:「基本」区含 位置(全局/项目)+ 项目文件夹 + 名称,下方直接编辑定义 JSON。</summary>
    public static Task<(string Name, string Json, string Project)?> AddMcpServerAsync(
        string? currentProject, string template, string jsonHint)
    {
        var name = new TextBox { Watermark = "服务器名称,如 chrome-devtools", MinWidth = 440 };
        var json = new TextBox
        {
            Text = template,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            MinHeight = 180,
            Height = 240,
        };
        var (scope, getProject) = ScopeSection(currentProject, defaultGlobal: string.IsNullOrWhiteSpace(currentProject),
            "所有项目可用 · ~/.config/mcp/mcp.json",
            "指定项目文件夹,写入它的 .mcp.json(可提交到 git)");
        var err = ErrText();
        var content = MkStack();
        content.Children.Add(Section("基本"));
        content.Children.Add(Field("位置", scope));
        content.Children.Add(Field("名称", name));
        content.Children.Add(Field("服务器定义", json));
        content.Children.Add(new TextBlock { Text = jsonHint, Opacity = 0.55, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(err);
        return ShowFormAsync<(string, string, string)?>("新增 MCP 服务器", content, () =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length == 0) { Fail(err); return null; }
            if (string.IsNullOrWhiteSpace(json.Text)) { Fail(err, "服务器定义不能为空。"); return null; }
            var proj = getProject();
            if (InvalidProjectPath(proj, err)) return null;
            err.IsVisible = false;
            return ((string, string, string)?)(n, json.Text, proj);
        });
    }

    /// <summary>pi 已内置的 API 协议类型(models.json 的 api 字段)。</summary>
    public static readonly string[] KnownApiTypes =
    {
        "openai-completions",
        "openai-responses",
        "azure-openai-responses",
        "openai-codex-responses",
        "anthropic-messages",
        "google-generative-ai",
        "google-vertex",
        "mistral-conversations",
        "bedrock-converse-stream",
        "pi-messages",
    };

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    /// <summary>新增/编辑服务商(models.json 的 providers 条目)。</summary>
    public static Task<ProviderEdit?> EditProviderAsync(ProviderInfo? initial)
    {
        var name = new TextBox
        {
            Text = initial?.Name ?? "",
            Watermark = "唯一标识,如 my-api(字母/数字/-/_/.)",
            MinWidth = 460,
            FontFamily = MonoFont,
            FontSize = 12,
        };
        var display = new TextBox
        {
            Text = initial?.DisplayName ?? "",
            Watermark = "可选;留空时用名称本身",
        };
        var baseUrl = new TextBox
        {
            Text = initial?.BaseUrl ?? "",
            Watermark = "如 http://127.0.0.1:8080/v1 或 https://api.example.com/v1",
            FontFamily = MonoFont,
            FontSize = 12,
        };
        var api = new AutoCompleteBox
        {
            Text = initial == null ? "openai-completions" : initial.Api ?? "",
            Watermark = "留空 = 用默认;常见:openai-completions / anthropic-messages",
            ItemsSource = KnownApiTypes,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
        };
        var apiKey = new TextBox
        {
            Text = initial?.ApiKey ?? "",
            Watermark = "明文 key / $ENV_VAR / !command;本地免鉴权服务可填占位符",
            FontFamily = MonoFont,
            FontSize = 12,
        };
        var headers = EditTextBox(initial?.HeadersJson ?? "", 70);
        var compat = EditTextBox(initial?.CompatJson ?? "", 70);

        var err = ErrText();
        var content = MkStack();
        content.Children.Add(Section("基本"));
        content.Children.Add(Field("名称(providers 的键)", name));
        content.Children.Add(Field("显示名称", display));
        content.Children.Add(Field("API 地址 baseUrl", baseUrl));
        content.Children.Add(Field("API 协议 api", api));
        content.Children.Add(Field("API Key", apiKey));
        content.Children.Add(Section("高级(可选)"));
        content.Children.Add(Field("自定义请求头 headers(JSON 对象)", headers));
        content.Children.Add(Field("兼容设置 compat(JSON 对象)", compat));
        content.Children.Add(err);

        return ShowFormAsync<ProviderEdit?>(initial == null ? "新增服务商" : $"编辑服务商 {initial.Name}", content, () =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length == 0) { Fail(err, "请填写服务商名称。"); return null; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9_.-]+$"))
            {
                Fail(err, "名称只允许字母、数字、下划线、点、连字符。");
                return null;
            }
            try
            {
                ModelSettingsService.ParseJsonObject(headers.Text, "自定义请求头");
                ModelSettingsService.ParseJsonObject(compat.Text, "兼容设置 compat");
            }
            catch (InvalidOperationException ex)
            {
                Fail(err, ex.Message);
                return null;
            }
            err.IsVisible = false;
            return new ProviderEdit(
                n,
                (display.Text ?? "").Trim(),
                (baseUrl.Text ?? "").Trim(),
                (api.Text ?? "").Trim(),
                (apiKey.Text ?? "").Trim(),
                headers.Text ?? "",
                compat.Text ?? "");
        });
    }

    /// <summary>新增/编辑模型(指定服务商下的 models[] 条目)。</summary>
    public static Task<ModelEdit?> EditModelAsync(CustomModelDef? initial)
    {
        static string S(double? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        static string L(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

        var id = new TextBox
        {
            Text = initial?.Id ?? "",
            Watermark = "模型 ID,请求时发给服务端的 model 参数",
            MinWidth = 460,
            FontFamily = MonoFont,
            FontSize = 12,
        };
        var displayName = new TextBox
        {
            Text = initial?.Name ?? "",
            Watermark = "可选;留空时用模型 ID",
        };
        var cbReasoning = new CheckBox
        {
            Content = "推理模型(reasoning)",
            IsChecked = initial?.Reasoning ?? false,
            Padding = new Thickness(2, 1),
        };
        var cbText = new CheckBox
        {
            Content = "文本",
            IsChecked = initial == null || initial.Input.Count == 0 || initial.Input.Contains("text"),
            Padding = new Thickness(2, 1),
        };
        var cbImage = new CheckBox
        {
            Content = "图像",
            IsChecked = initial != null && initial.Input.Contains("image"),
            Padding = new Thickness(2, 1),
        };
        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        inputRow.Children.Add(cbReasoning);
        inputRow.Children.Add(new TextBlock { Text = "输入类型:", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        inputRow.Children.Add(cbText);
        inputRow.Children.Add(cbImage);

        var ctx = NumberBox(initial == null ? "131072" : L(initial.ContextWindow), "如 131072");
        var maxTok = NumberBox(initial == null ? "32768" : L(initial.MaxTokens), "如 32768");
        var limitsRow = NumRow(("上下文窗口 contextWindow", ctx), ("最大输出 maxTokens", maxTok));

        var cIn = NumberBox(initial == null ? "0" : S(initial.CostInput));
        var cOut = NumberBox(initial == null ? "0" : S(initial.CostOutput));
        var cRd = NumberBox(initial == null ? "0" : S(initial.CostCacheRead));
        var cWr = NumberBox(initial == null ? "0" : S(initial.CostCacheWrite));
        var costRow = NumRow(("输入", cIn), ("输出", cOut), ("缓存读", cRd), ("缓存写", cWr));

        var api = new AutoCompleteBox
        {
            Text = initial?.Api ?? "",
            Watermark = "留空 = 用服务商默认",
            ItemsSource = KnownApiTypes,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
        };
        var baseUrl = new TextBox
        {
            Text = initial?.BaseUrl ?? "",
            Watermark = "留空 = 用服务商默认",
            FontFamily = MonoFont,
            FontSize = 12,
        };

        var samplingParams = EditTextBox(
            initial == null || initial.SamplingParams == null || initial.SamplingParams.Count == 0
                ? ""
                : initial.SamplingParams.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            110,
            "{\"temperature\": 0.7, \"top_p\": 0.9, \"top_k\": 40}");
        var headers = EditTextBox(initial?.HeadersJson ?? "", 70);

        var err = ErrText();
        var content = MkStack();
        content.Children.Add(Section("基本"));
        content.Children.Add(Field("模型 ID", id));
        content.Children.Add(Field("显示名称", displayName));
        content.Children.Add(inputRow);
        content.Children.Add(Section("限制与费用(费用 = 每百万 token 的美元数)"));
        content.Children.Add(limitsRow);
        content.Children.Add(costRow);
        content.Children.Add(Section("覆盖服务商默认(可选)"));
        content.Children.Add(Field("API 协议 api", api));
        content.Children.Add(Field("API 地址 baseUrl", baseUrl));
        content.Children.Add(Section("采样参数 samplingParams(手动填写,自动拼接进文件)"));
        content.Children.Add(Field("采样参数(JSON 对象,留空 = 不传,用服务端默认)", samplingParams));
        content.Children.Add(new TextBlock
        {
            Text = "不同模型参数不同,按需手填,如 temperature / top_p / top_k / repetition_penalty / min_p…;\n留空时不写入 samplingParams 字段,请求走服务端与 pi 的默认值。",
            Opacity = 0.55,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(Field("模型级请求头 headers(JSON 对象,可选)", headers));
        content.Children.Add(err);

        return ShowFormAsync<ModelEdit?>(initial == null ? "新增模型" : $"编辑模型 {initial.Id}", content, () =>
        {
            var mid = (id.Text ?? "").Trim();
            if (mid.Length == 0) { Fail(err, "请填写模型 ID。"); return null; }
            var hasText = cbText.IsChecked == true;
            var hasImage = cbImage.IsChecked == true;
            if (!hasText && !hasImage) { Fail(err, "至少勾选一种输入类型(文本/图像)。"); return null; }
            try
            {
                ModelSettingsService.ParseOptLong(ctx.Text, "上下文窗口");
                ModelSettingsService.ParseOptLong(maxTok.Text, "最大输出");
                ModelSettingsService.ParseOptDouble(cIn.Text, "输入费用");
                ModelSettingsService.ParseOptDouble(cOut.Text, "输出费用");
                ModelSettingsService.ParseOptDouble(cRd.Text, "缓存读费用");
                ModelSettingsService.ParseOptDouble(cWr.Text, "缓存写费用");
                ModelSettingsService.ParseJsonObject(samplingParams.Text, "采样参数 samplingParams");
                ModelSettingsService.ParseJsonObject(headers.Text, "模型级请求头");
            }
            catch (InvalidOperationException ex)
            {
                Fail(err, ex.Message);
                return null;
            }
            err.IsVisible = false;
            return new ModelEdit(
                mid,
                (displayName.Text ?? "").Trim(),
                cbReasoning.IsChecked == true,
                hasText,
                hasImage,
                ctx.Text ?? "",
                maxTok.Text ?? "",
                cIn.Text ?? "",
                cOut.Text ?? "",
                cRd.Text ?? "",
                cWr.Text ?? "",
                (api.Text ?? "").Trim(),
                (baseUrl.Text ?? "").Trim(),
                samplingParams.Text ?? "",
                headers.Text ?? "");
        });
    }

    private static TextBox EditTextBox(string text, double height, string? watermark = null)
    {
        var tb = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            FontSize = 12,
            Height = height,
        };
        if (watermark != null) tb.Watermark = watermark;
        return tb;
    }

    private static TextBox NumberBox(string text, string? watermark = null)
    {
        var tb = new TextBox { Text = text, MinWidth = 90, FontFamily = MonoFont, FontSize = 12 };
        if (watermark != null) tb.Watermark = watermark;
        return tb;
    }

    /// <summary>一行「标签 + 输入框」迷你组合(数字/采样参数行用)。</summary>
    private static StackPanel NumRow(params (string Label, TextBox Box)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var (label, box) in items)
        {
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.65 });
            col.Children.Add(box);
            row.Children.Add(col);
        }
        return row;
    }

    /// <summary>安装 Pi 包表单:「基本」区含 位置(全局/项目)+ 项目文件夹 + 包来源。</summary>
    public static Task<(string Source, string Project)?> InstallPackageAsync(string? currentProject)
    {
        var source = new TextBox { Watermark = "npm:包名 / git:仓库 / 本地路径", MinWidth = 440 };
        var (scope, getProject) = ScopeSection(currentProject, defaultGlobal: true,
            "所有项目可用 · ~/.pi/agent/settings.json",
            "指定项目文件夹,写入它的 .pi/settings.json(需项目信任)");
        var err = ErrText();
        var content = MkStack();
        content.Children.Add(Section("基本"));
        content.Children.Add(Field("位置", scope));
        content.Children.Add(Field("包来源", source));
        content.Children.Add(err);
        return ShowFormAsync<(string, string)?>("安装 Pi 包", content, () =>
        {
            var s = (source.Text ?? "").Trim();
            if (s.Length == 0) { Fail(err); return null; }
            var proj = getProject();
            if (InvalidProjectPath(proj, err)) return null;
            err.IsVisible = false;
            return ((string, string)?)(s, proj);
        });
    }

    /// <summary>项目历史管理的结果:保留列表 / 被移除列表 / 是否同步删除项目预设。</summary>
    public sealed record ProjectHistoryResult(List<string> Remaining, List<string> Removed, bool DeletePresets);

    /// <summary>项目历史管理:逐条移除或清空,可选同步删除被移除项目的遗留项目预设。</summary>
    public static Task<ProjectHistoryResult?> ManageProjectsAsync(IReadOnlyList<string> projects, string currentProject)
    {
        var remaining = projects.ToList();
        var removed = new List<string>();
        var rows = new StackPanel { Spacing = 6 };

        void AddRow(string path)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var txt = new TextBlock
            {
                Text = path,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 400,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(txt, path);
            row.Children.Add(txt);
            if (path.Equals(currentProject, StringComparison.OrdinalIgnoreCase))
            {
                row.Children.Add(new Border
                {
                    Classes = { "badge", "badge-info" },
                    Child = new TextBlock { Text = "当前" },
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (Directory.Exists(Path.Combine(path, ".pi", "td-pi", "presets")))
            {
                row.Children.Add(new Border
                {
                    Classes = { "badge", "badge-warn" },
                    Child = new TextBlock { Text = "有项目预设" },
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var remove = new Button { Content = "✖ 移除", Classes = { "danger" }, Padding = new Thickness(8, 3) };
            remove.Click += (_, _) =>
            {
                remaining.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                removed.Add(path);
                rows.Children.Remove(row);
            };
            row.Children.Add(remove);
            rows.Children.Add(row);
        }
        foreach (var p in projects) AddRow(p);

        var clearAll = new Button
        {
            Content = "🗑 清空全部历史",
            Classes = { "danger" },
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        clearAll.Click += (_, _) =>
        {
            removed.AddRange(remaining);
            remaining.Clear();
            rows.Children.Clear();
        };

        var delPresets = new CheckBox
        {
            Content = "同时删除被移除项目的项目预设(.pi\\td-pi 整个文件夹)",
            IsChecked = true,
        };

        var content = MkStack();
        content.Children.Add(new TextBlock
        {
            Text = "仅从下拉历史中移除,不会删除项目本身的代码文件。\n若移除的项目带有「有项目预设」徽章,勾选下方选项后其遗留预设也会一并删掉。",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(clearAll);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 380 });
        content.Children.Add(delPresets);

        return ShowFormAsync<ProjectHistoryResult?>("项目历史管理", content, () =>
            new ProjectHistoryResult(remaining.ToList(), removed.ToList(), delPresets.IsChecked == true), "完成");
    }

    // ---------- 表单构建辅助 ----------

    /// <summary>
    /// 「位置」区:全局 / 项目单选。选「项目」时可直接指定项目文件夹(输入框 + 浏览),
    /// 不依赖顶部当前项目;输入框预填当前项目作为默认值。返回面板与项目文件夹取值函数(全局时返回 "")。
    /// </summary>
    private static (StackPanel Panel, Func<string> GetProject) ScopeSection(
        string? currentProject, bool defaultGlobal, string globalHint, string projectHint)
    {
        var hasCurrent = !string.IsNullOrWhiteSpace(currentProject);
        var rbGlobal = new RadioButton
        {
            GroupName = "td-scope",
            IsChecked = defaultGlobal || !hasCurrent,
            Content = ScopeLabel("🌐 全局", globalHint),
            Padding = new Thickness(4, 3),
        };
        var rbProject = new RadioButton
        {
            GroupName = "td-scope",
            IsChecked = hasCurrent && !defaultGlobal,
            Content = ScopeLabel("📁 项目", projectHint),
            Padding = new Thickness(4, 3),
        };
        var projectBox = new TextBox
        {
            Text = currentProject ?? "",
            Watermark = "项目文件夹完整路径(可随意指定,不必是顶部当前项目)",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MinWidth = 380,
        };
        var browse = new Button { Content = "📁 浏览…", Classes = { "ghost" }, VerticalAlignment = VerticalAlignment.Center };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(browse) is not Window w) return;
            var folders = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择项目文件夹",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } p && p.Length > 0)
                projectBox.Text = p;
        };
        var projectRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(24, 0, 0, 0),
        };
        projectRow.Children.Add(projectBox);
        projectRow.Children.Add(browse);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(rbGlobal);
        panel.Children.Add(rbProject);
        panel.Children.Add(projectRow);
        return (panel, () => rbProject.IsChecked == true ? projectBox.Text?.Trim() ?? "" : "");
    }

    /// <summary>选了项目时校验路径:非空且为完整路径。不合法时显示错误并返回 true。</summary>
    private static bool InvalidProjectPath(string proj, TextBlock err)
    {
        if (proj.Length == 0) return false; // 全局
        if (!Path.IsPathRooted(proj))
        {
            Fail(err, "项目文件夹请填写完整路径(或点「浏览…」选择)。");
            return true;
        }
        return false;
    }

    private static StackPanel ScopeLabel(string title, string hint)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(hint))
            sp.Children.Add(new TextBlock { Text = hint, Opacity = 0.55, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static StackPanel MkStack() => new() { Spacing = 12 };

    private static TextBlock Section(string text) => new() { Text = text, Classes = { "sectionTitle" } };

    private static StackPanel Field(string label, Control input)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
        sp.Children.Add(input);
        return sp;
    }

    /// <summary>标记:ShowFormAsync 靠它识别错误提示,出现时自动滚到底部。</summary>
    private const string FormErrClass = "formErr";

    private static TextBlock ErrText()
    {
        var tb = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        tb.Classes.Add(FormErrClass);
        return tb;
    }

    /// <summary>显示校验错误并让表单保持打开。</summary>
    private static void Fail(TextBlock err, string message = "请填写必填项。")
    {
        err.Text = message;
        err.IsVisible = true;
    }

    /// <summary>
    /// 通用表单窗口:内容区可滚动、按钮固定在底部 —— 表单过高(如「编辑模型」)时
    /// 保存按钮不会被截掉。确定按钮执行 complete,返回 null 则不关闭(校验失败)。
    /// </summary>
    private static async Task<T?> ShowFormAsync<T>(string title, StackPanel content, Func<T?> complete, string okText = "确定")
    {
        var ok = MkButton(okText);
        var cancel = MkButton("取消");
        ok.IsDefault = true;    // Enter = 确定(多行框内回车仍是换行,不受影响)
        cancel.IsCancel = true; // Esc = 取消
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        // 行 0 = 可滚动内容;行 1 = 按钮固定底部
        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        buttons.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(scroll, 0);
        Grid.SetRow(buttons, 1);
        root.Children.Add(scroll);
        root.Children.Add(buttons);

        // 校验错误提示(内容区底部)出现时自动滚到底,避免看不到报错
        var errBlock = content.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains(FormErrClass));
        if (errBlock != null)
        {
            errBlock.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && errBlock.IsVisible)
                    Dispatcher.UIThread.Post(() => scroll.ScrollToEnd());
            };
        }

        // 高度上限按屏幕工作区自适应(小屏也不截按钮;内容超出则滚动)
        double maxH = 720;
        try
        {
            var screen = Owner?.Screens?.ScreenFromWindow(Owner);
            if (screen != null)
                maxH = Math.Min(720, screen.WorkingArea.Height / screen.Scaling - 80);
        }
        catch
        {
            // 取不到屏幕信息就用默认值
        }

        var dialog = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            SizeToContent = SizeToContent.Width,
            MinHeight = 420,
            MaxWidth = 780,
            MaxHeight = Math.Max(420, maxH),
            Content = root,
        };
        ok.Click += (_, _) => { var r = complete(); if (r is not null) dialog.Close(r); };
        cancel.Click += (_, _) => dialog.Close(default);
        return await dialog.ShowDialog<T>(Owner!);
    }

    private static async Task<bool> ShowBoolAsync(string title, string message, string okText, string? cancelText)
    {
        var label = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        };
        var dialog = Build(title, label, okText, cancelText);
        return await dialog.ShowDialog<bool>(Owner!);
    }

    /// <summary>ShowDialog 包装:ok 返回 true,cancel/关闭 返回 false。</summary>
    private static Task<bool> ShowAsync(string title, Control content, string okText, string? cancelText = "取消")
    {
        var dialog = Build(title, content, okText, cancelText);
        return dialog.ShowDialog<bool>(Owner!);
    }

    private static Window Build(string title, Control content, string okText, string? cancelText)
    {
        var ok = MkButton(okText);
        var cancel = cancelText == null ? null : MkButton(cancelText);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (cancel != null) buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var dialog = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            SizeToContent = SizeToContent.Width,
            MaxWidth = 780,
            MaxHeight = 680,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children = { content, buttons },
            },
        };

        ok.Click += (_, _) => dialog.Close(true);
        if (cancel != null)
            cancel.Click += (_, _) => dialog.Close(false);
        else
            dialog.Closed += (_, _) => { /* 只有一个按钮,点 X 等同确定 */ };
        if (cancelText == null)
        {
            // 单按钮对话框:窗口关闭也返回 true
            dialog.Closing += (_, e) =>
            {
                if (!dialog.IsVisible) return;
            };
        }
        return dialog;
    }

    private static Button MkButton(string text) => new()
    {
        Content = text,
        MinWidth = 88,
        Padding = new Avalonia.Thickness(16, 6),
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };
}
