# td-pi —— Pi 管理器

基于 **Avalonia (.NET 9)** 的 Windows 桌面管理器,为 [pi](https://github.com/earendil-works/pi-coding-agent) coding agent 提供图形化管理能力:

| 模块 | 功能 |
|---|---|
| 🚀 预设管理 | **预设 = AI 身份与能力的完整设定**(保存在项目中)。SillyTavern 式消息列表编辑(系统/用户/AI 消息自由增删、调序、逐条开关)、注入预览、暴露视图、命令行启动命令一键复制 |
| ▶ 启动预设 | 查看预设注入详情并**一键启动**:自动打开新终端窗口、进入项目目录、编译注入 |
| 💬 会话记录 | 查看 pi 全部历史会话:**自动识别是否使用预设**(绿色徽章=预设名+注入来源),支持筛选/搜索、查看对话内容、**一键恢复会话**、批量删除 |
| 📘 技能管理 | 全局(`~/.pi/agent/skills`)与项目(`.pi/skills`、`.agents/skills`)技能的查看、新建、编辑、**启停**、删除,frontmatter 校验与缺失描述警告 |
| 🔌 MCP 管理 | 基于 pi-mcp-adapter 配置体系:服务器增删改、**启停**(写入适配器覆盖层,与 `/mcp-adapter enable\|disable` 互通)、适配器安装/更新 |
| 🧩 插件管理 | Pi 包(install/remove/update)、本地扩展(重命名 `.disabled` 启停)、settings 路径条目(`+`/`-` 前缀),全局与项目两个作用域 |
| 🧷 自定义模型 | 图形化编辑 `~/.pi/agent/models.json`:服务商(URL/Key/请求头/compat)与模型(contextWindow/费用/采样参数)增删改,保留未识别扩展字段 |
| 🧷 托盘 | 像素风 π 托盘图标:左键唤出窗口;关闭窗口隐藏到托盘;单实例 |

## 运行依赖

| 依赖 | 说明 |
|---|---|
| Windows 10 / 11(x64) | 唯一支持的操作系统 |
| [pi](https://github.com/earendil-works/pi-coding-agent) | 需已安装且在 PATH 中:`npm install -g @earendil-works/pi-coding-agent`,并完成至少一个模型供应商的登录/配置 |
| .NET 运行时 | **不需要** —— 发布为自包含(single-dir),解压即用 |
| Windows Terminal(可选) | 已安装时预设启动使用 wt 窗口,否则回退 cmd |
| pi-mcp-adapter(可选) | 使用预设专属 MCP 配置时需要,可在 MCP 页一键安装 |

## 安装

1. 从 [Releases](../../releases) 下载 `td-pi-vX.Y.Z-win-x64.zip`,解压到任意目录;
2. 运行 `td-pi.exe`;
3. 首次使用预设功能时,到「诊断」页点击部署 **td-pi-preset 扩展**(由管理器自动安装/更新到 `~/.pi/agent/extensions/td-pi-preset/`)。

## 预设系统(核心设计)

一个预设 = 一个文件夹,只存在于项目中:

```
<项目>/.pi/td-pi/presets/<名称>/
├── preset.json          # 消息列表内联存储
└── mcp.json             # 本预设专属 MCP 服务器(可选)
```

消息列表中三类消息的注入机制与压缩安全性:

| 角色 | 注入机制 | 压缩安全性 |
|---|---|---|
| 🟦 系统 | 编译文件经 `--system-prompt` 注入系统提示词层(**覆盖** pi 默认系统提示词,原文注入,无任何标记) | **永不压缩**(系统提示词不参与压缩) |
| 🟩 用户 | td-pi-preset 扩展 `context` 事件 → 每次调用 LLM 前拼到真实对话前 | **永不压缩**(不写入会话,每请求重拼) |
| 🟪 AI | 同上 | **永不压缩**(同上) |

上下文消息与 SillyTavern 完全同构:存在预设 JSON、不在会话历史里,每次请求动态拼接 —— 上下文压缩只作用于会话条目,对注入内容不可见。

### 生效方式

- **管理器启动**:「▶ 启动预设」页选中预设 → 查看注入详情 → 一键启动(自动进入项目目录);
- **命令行启动**:预设管理页复制编译好的 pi 命令,在任意终端手动运行;
- **恢复会话**:「会话记录」页一键恢复,或手动 `pi --continue` —— 扩展从会话记录自动延续注入,与管理器启动互认、不重复。

## 预设内容之外的可选项(每预设独立)

- **扩展**:叠加模式(附加 `-e`)或独占模式(`--no-extensions`,仅保留基础设施 + 预设声明);
- **技能**:叠加模式(附加 `--skill`)或独占模式(`--no-skills`);
- **MCP**:`--mcp-config` 加载预设专属服务器,支持用 `{"disabled":true}` 覆盖禁用全局同名服务器;
- **开场消息**:走 argv 位置参数(会被压缩,关键信息请放系统/上下文消息)。

## 安全与边界

- 所有启停操作**无损可逆**(重命名 / 配置字段),不删用户数据;
- MCP 启停写入适配器覆盖层,不污染共享配置文件(与 adapter 自身行为一致);
- 运行中的 pi 窗口 = 启动那一刻的配置快照;改预设需重新启动窗口(`--continue` 无损续会话)。

## 数据位置

| 数据 | 位置 |
|---|---|
| 预设 | `<项目>/.pi/td-pi/presets/` |
| 会话记录 | `~/.pi/agent/sessions/`(pi 管理,td-pi 只读+删除) |
| 管理器设置/编译缓存 | `%LOCALAPPDATA%\td-pi\` |
| td-pi-preset 扩展 | `~/.pi/agent/extensions/td-pi-preset/`(诊断页一键部署) |

## 从源码构建

依赖:.NET 9 SDK。

```cmd
git clone https://github.com/Td0000000/td-pi.git
cd td-pi
publish.cmd        # 自包含发布 → 输出目录见脚本内 OUT 变量
```

或手动:

```cmd
dotnet publish src\TdPi.App\TdPi.App.csproj -c Release -r win-x64 --self-contained -o <输出目录>
```

开发运行:`dotnet run --project src\TdPi.App\TdPi.App.csproj`

目录结构:

```
├── src/TdPi.App/      # Avalonia 桌面应用(ViewModels + Views)
├── src/TdPi.Core/     # 服务层(预设/技能/MCP/插件/模型/会话/启动)
├── extension/td-pi-preset/index.ts   # pi 扩展(嵌入 App 资源,由诊断页部署)
├── tools/make_icon.py # 像素风 π 图标生成(纯 Python)
└── docs/架构设计.md
```

图标:像素风 π 多尺寸 ICO/PNG,字形取自 [pixelarticons](https://github.com/halfmage/pixelarticons)(MIT),配色 Catppuccin Mocha;exe/窗口/托盘共用。
