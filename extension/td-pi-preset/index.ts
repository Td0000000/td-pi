// td-pi-preset v1.4.0 —— td-pi 预设系统扩展(由 td-pi 管理器部署)
//
// v1.4.0:系统提示词只含用户填写的系统消息原文 —— 去掉注入标记与标题/说明行;
//         管理器启动(TD_PI_PRESET 存在)时系统提示词已由 --system-prompt 注入,扩展不再改动。
//
// 职责:
//  1. context 事件:把预设的用户 / AI 消息作为上下文消息拼到真实对话之前 ——
//     不写入会话、不是会话条目,每次 LLM 调用前重新拼装,因此永远不被上下文压缩
//  2. 手动 pi --continue / --session 恢复预设会话时,运行时补注入系统消息原文
//  3. session_start:把生效预设记录到会话(供恢复延续)
//  4. 状态 widget:编辑器上方展示当前生效预设
//
// 生效方式(唯一):
//  - td-pi 管理器「启动预设」页启动 → 环境变量 TD_PI_PRESET(预设名)+ TD_PI_PRESET_DIR(预设文件夹),
//    系统提示词已由管理器经 --system-prompt 注入(系统消息原文)
//  - 恢复使用过预设的会话(--continue / pi --session)→ 从会话记录延续
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

const VERSION = "1.4.0";
const RECORD_TYPE = "td-pi-preset";

// ---------------- 类型 ----------------

interface Fragment {
    file: string;
    label?: string;
    enabled?: boolean;
}

interface PresetMessage {
    id?: string;
    role: string; // system | user | assistant
    label?: string;
    content: string;
    enabled?: boolean;
}

interface PresetDef {
    name?: string;
    description?: string;
    model?: string;
    sessionName?: string;
    messages?: PresetMessage[];
    system?: (string | Fragment)[]; // 旧字段,迁移兼容
    assistantExamples?: (string | Fragment)[];
    fragments?: (string | Fragment)[];
    sharedFragments?: (string | Fragment)[];
    openingMessage?: string;
    extensions?: string[];
    extensionsMode?: string;
    skills?: string[];
    skillsMode?: string;
    mcpConfig?: string;
    notes?: string;
}

interface ResolvedPreset {
    name: string;
    dir: string;
    def: PresetDef;
    /** 规范化后的消息列表(含旧字段迁移) */
    messages: PresetMessage[];
}

// ---------------- 工具函数 ----------------

const agentDir = (): string =>
    process.env.PI_CODING_AGENT_DIR || path.join(os.homedir(), ".pi", "agent");

function debugLog(msg: string): void {
    if (!process.env.TD_PI_DEBUG) return;
    try {
        fs.appendFileSync(path.join(os.tmpdir(), "td-pi-ext.log"), `[${new Date().toISOString()}] ${msg}\n`);
    } catch {
        /* ignore */
    }
}

function readJson(file: string): Record<string, unknown> | undefined {
    try {
        const text = fs.readFileSync(file, "utf-8").trim();
        if (!text) return undefined;
        return JSON.parse(stripJsonComments(text)) as Record<string, unknown>;
    } catch {
        return undefined;
    }
}

function stripJsonComments(text: string): string {
    return text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
}

function normalizeFragments(list: unknown): Fragment[] {
    if (!Array.isArray(list)) return [];
    const out: Fragment[] = [];
    for (const item of list) {
        if (typeof item === "string") {
            out.push({ file: item, enabled: true });
        } else if (item && typeof item === "object") {
            const f = item as Fragment;
            out.push({ file: f.file ?? "", label: f.label, enabled: f.enabled !== false });
        }
    }
    return out;
}

function readFragmentContent(presetDir: string, file: string): string {
    try {
        const abs = path.isAbsolute(file) ? file : path.resolve(presetDir, file);
        if (!fs.existsSync(abs)) return "";
        return fs.readFileSync(abs, "utf-8").replace(/\s+$/, "");
    } catch {
        return "";
    }
}

/** 旧片段字段 → 内联消息(与 C# MigrateLegacyFragments 一致)。 */
function migrateMessages(def: PresetDef, dir: string): PresetMessage[] {
    const messages: PresetMessage[] = (def.messages ?? []).filter(
        (m) => m && typeof m === "object" && typeof m.content === "string",
    );
    if (messages.length > 0) return messages;
    if (!def.system?.length && !def.sharedFragments?.length && !def.fragments?.length && !def.assistantExamples?.length) {
        return [];
    }
    const baseName = (f: Fragment): string => {
        const b = path.basename(f.file ?? "");
        const idx = b.lastIndexOf(".");
        return idx > 0 ? b.slice(0, idx) : b;
    };
    for (const f of normalizeFragments(def.system)) {
        messages.push({
            role: "system",
            label: f.label?.trim() || baseName(f),
            content: readFragmentContent(dir, f.file ?? ""),
            enabled: f.enabled !== false,
        });
    }
    for (const f of normalizeFragments(def.sharedFragments ?? def.fragments)) {
        messages.push({
            role: "system",
            label: f.label?.trim() || baseName(f),
            content: readFragmentContent(dir, f.file ?? ""),
            enabled: f.enabled !== false,
        });
    }
    for (const f of normalizeFragments(def.assistantExamples)) {
        messages.push({
            role: "assistant",
            label: f.label?.trim() || baseName(f),
            content: readFragmentContent(dir, f.file ?? ""),
            enabled: f.enabled !== false,
        });
    }
    return messages;
}

// ---------------- 预设发现与解析 ----------------

function presetDirs(name: string, cwd: string): string[] {
    const dirs: string[] = [];
    // 管理器启动时注入的精确预设文件夹(最高优先)
    if (process.env.TD_PI_PRESET_DIR) dirs.push(process.env.TD_PI_PRESET_DIR);
    // 项目预设
    dirs.push(path.join(cwd, ".pi", "td-pi", "presets", name));
    return dirs;
}

function loadPreset(name: string, cwd: string): ResolvedPreset | undefined {
    for (const dir of presetDirs(name, cwd)) {
        const file = path.join(dir, "preset.json");
        if (!fs.existsSync(file)) continue;
        const def = readJson(file) as PresetDef | undefined;
        if (!def) continue;
        return { name: def.name || name, dir, def, messages: migrateMessages(def, dir) };
    }
    return undefined;
}

// ---------------- 注入构建(与 C# PresetCompiler 字节级一致) ----------------

/** 启用的系统消息(有序)。 */
function systemMessages(p: ResolvedPreset): PresetMessage[] {
    return p.messages.filter((m) => m.enabled !== false && m.role === "system");
}

/** 启用的上下文消息(user/assistant,有序)。 */
function contextMessages(p: ResolvedPreset): PresetMessage[] {
    return p.messages.filter((m) => m.enabled !== false && (m.role === "user" || m.role === "assistant"));
}

/**
 * 系统提示词内容:仅启用的系统消息「原文」按顺序拼接(消息之间空一行),
 * 不含任何标记、标题、说明文字 —— 系统提示词里只有用户填写的内容。
 */
function buildBlock(p: ResolvedPreset): string {
    return systemMessages(p)
        .map((m) => (m.content ?? "").replace(/\s+$/, ""))
        .filter((c) => c.length > 0)
        .join("\n\n");
}

// ---------------- 上下文消息构造 ----------------

interface FakeUserMessage {
    role: "user";
    content: string;
    timestamp: number;
}

interface FakeAssistantMessage {
    role: "assistant";
    content: { type: "text"; text: string }[];
    api: string;
    provider: string;
    model: string;
    usage: { input: number; output: number; cacheRead: number; cacheWrite: number };
    stopReason: string;
    timestamp: number;
}

function fakeUserMessage(content: string): FakeUserMessage {
    return { role: "user", content, timestamp: 0 };
}

function fakeAssistantMessage(content: string): FakeAssistantMessage {
    return {
        role: "assistant",
        content: [{ type: "text", text: content }],
        api: "openai-completions",
        provider: "td-pi-preset",
        model: "preset-injection",
        usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
        stopReason: "stop",
        timestamp: 0,
    };
}

// ---------------- 生效预设解析 ----------------
// 优先级:环境变量 TD_PI_PRESET(管理器启动)> 会话记录(--continue / --session 延续)

function resolveActive(
    cwd: string,
    sessionEntries?: ReadonlyArray<{ type?: string; customType?: string; data?: { name?: string } }>,
): ResolvedPreset | undefined {
    if (process.env.TD_PI_PRESET) {
        const p = loadPreset(process.env.TD_PI_PRESET, cwd);
        if (p) return p;
    }
    if (sessionEntries) {
        const name = recordFromSession(sessionEntries);
        if (name) {
            const p = loadPreset(name, cwd);
            if (p) return p;
        }
    }
    return undefined;
}

function recordFromSession(entries: ReadonlyArray<{ type?: string; customType?: string; data?: unknown }>): string | undefined {
    for (let i = entries.length - 1; i >= 0; i--) {
        const e = entries[i];
        if (e?.type === "custom" && (e as { customType?: string }).customType === RECORD_TYPE) {
            const data = (e as { data?: { name?: string } }).data;
            if (data && typeof data.name === "string") return data.name;
        }
    }
    return undefined;
}

// ---------------- 扩展安装 ----------------

export default function (pi: ExtensionAPI): void {
    const currentBranch = (ctx: { sessionManager?: { getBranch?(): unknown } }): Array<Record<string, unknown>> => {
        try {
            const branch = ctx.sessionManager?.getBranch?.();
            return Array.isArray(branch) ? (branch as Array<Record<string, unknown>>) : [];
        } catch {
            return [];
        }
    };

    // 状态展示(编辑器上方小部件)
    const updateWidget = (
        ctx: { ui: { setWidget(k: string, c: string[] | undefined): void }; cwd: string },
        active?: ResolvedPreset,
    ): void => {
        try {
            active = active ?? resolveActive(ctx.cwd, currentBranch(ctx));
            if (!active) {
                ctx.ui.setWidget("td-pi", undefined);
                return;
            }
            const sys = systemMessages(active).length;
            const ctxN = contextMessages(active).length;
            const lines = [`td-pi 预设:${active.name}`];
            if (active.def.model) lines.push(`模型:${active.def.model}`);
            if (sys > 0 || ctxN > 0)
                lines.push(`注入:系统消息 ${sys} 条 + 上下文消息 ${ctxN} 条(均不会被压缩)`);
            ctx.ui.setWidget("td-pi", lines);
        } catch {
            /* ignore */
        }
    };

    // ---------- before_agent_start:运行时补注入系统提示词(仅手动恢复会话场景) ----------
    // 管理器启动(TD_PI_PRESET 存在)时,系统提示词已由 --system-prompt 注入系统消息原文,
    // 扩展不再改动;仅当用户手动 pi --continue / --session 恢复预设会话时,在此补上。
    pi.on("before_agent_start", (event, ctx) => {
        try {
            if (process.env.TD_PI_PRESET) return;

            const active = resolveActive(ctx.cwd, currentBranch(ctx));
            if (!active) return;
            const block = buildBlock(active);
            if (!block) return;

            const opts = event.systemPromptOptions;
            const current = opts.customPrompt ?? "";
            if (current.includes(block)) {
                debugLog(`system content for ${active.name} already present`);
                return;
            }
            opts.customPrompt = current ? current + "\n\n" + block : block;
            debugLog(`injected system content for ${active.name} (${block.length} chars)`);
        } catch (err) {
            debugLog(`before_agent_start error: ${String(err)}`);
        }
    });

    // ---------- context:上下文消息(user / assistant) ----------
    // 每次主 LLM 调用前触发(压缩摘要调用不经过此路径)。
    // 拼在真实对话之前;不写入会话,压缩对其不可见 → 永不被压缩。
    pi.on("context", (event, ctx) => {
        try {
            const active = resolveActive(ctx.cwd, currentBranch(ctx));
            if (!active) return;
            const msgs = contextMessages(active);
            if (msgs.length === 0) return;

            const fake = msgs.map((m) =>
                m.role === "user"
                    ? fakeUserMessage(m.content ?? "")
                    : fakeAssistantMessage(m.content ?? ""),
            );
            debugLog(`context: prepending ${fake.length} context messages`);
            return { messages: [...fake, ...event.messages] };
        } catch (err) {
            debugLog(`context error: ${String(err)}`);
        }
    });

    // ---------- session_start:记录生效预设(供 --continue 延续) ----------
    pi.on("session_start", (_event, ctx) => {
        try {
            const active = resolveActive(ctx.cwd, currentBranch(ctx));
            updateWidget(ctx, active);
            if (!active) return;
            pi.appendEntry(RECORD_TYPE, {
                name: active.name,
                version: VERSION,
                source: "session_start",
                messages: { system: systemMessages(active).length, context: contextMessages(active).length },
            });
            ctx.ui.setStatus("td-pi", `预设:${active.name}`);
            debugLog(`session_start recorded preset ${active.name}`);
        } catch (err) {
            debugLog(`session_start error: ${String(err)}`);
        }
    });

    pi.on("session_shutdown", (_event, ctx) => {
        try {
            ctx.ui.setStatus("td-pi", undefined);
            ctx.ui.setWidget("td-pi", undefined);
        } catch {
            /* ignore */
        }
    });

    debugLog(`td-pi-preset v${VERSION} loaded`);
}
