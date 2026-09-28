using System.Diagnostics;
using System.Text;
using TdPi.Core.Models;

namespace TdPi.Core.Services;

/// <summary>启动 pi:生成批处理(含环境变量注入)并在终端窗口中运行。</summary>
public class LaunchService
{
    private readonly PiEnvironment _env;

    public LaunchService(PiEnvironment env) => _env = env;

    /// <summary>
    /// 构建恢复会话的启动参数:工作目录 = 会话的项目目录,pi --session &lt;文件&gt;。
    /// 若会话使用了预设且预设仍存在,一并恢复预设注入(系统提示词块 + 环境变量),
    /// 与管理器启动行为一致(扩展也会从会话记录延续,双通道互相识别不重复)。
    /// </summary>
    public CompiledLaunch BuildResumeLaunch(SessionInfo session, PresetInfo? preset)
    {
        var args = new List<string> { "--session", session.File };
        string? blockFile = null;
        var environment = new Dictionary<string, string>();
        var presetName = "恢复会话";
        var presetDir = session.Cwd;

        if (preset != null)
        {
            presetName = preset.Preset.Name;
            presetDir = preset.Directory;
            var block = PresetCompiler.BuildSystemBlock(preset.Preset);
            if (block.Length > 0)
            {
                blockFile = PresetCompiler.CompiledBlockPath(_env.CompiledDir, preset.Preset.Name, preset.Directory);
                File.WriteAllText(blockFile, block);
                // --system-prompt:覆盖 pi 默认系统提示词(与管理器启动行为一致)
                args.AddRange(new[] { "--system-prompt", blockFile });
            }
            environment["TD_PI_PRESET"] = preset.Preset.Name;
            environment["TD_PI_PRESET_DIR"] = preset.Directory;
        }

        return new CompiledLaunch
        {
            PresetName = presetName,
            PresetDirectory = presetDir,
            ProjectDirectory = session.Cwd,
            SystemBlock = blockFile != null ? File.ReadAllText(blockFile) : "",
            SystemBlockFile = blockFile,
            Arguments = args,
            OpeningMessage = "",
            Environment = environment,
            Warnings = new List<string>(),
        };
    }

    /// <summary>写入启动批处理文件并返回其路径。</summary>
    public string WriteBatch(CompiledLaunch launch, string? piCommand)
    {
        piCommand ??= _env.PiCommand ?? "pi";
        Directory.CreateDirectory(_env.LaunchDir);
        var batch = Path.Combine(_env.LaunchDir, $"launch-{Guid.NewGuid():N}.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"title td-pi - {launch.PresetName} - {Path.GetFileName(launch.ProjectDirectory)}");
        sb.AppendLine();
        sb.AppendLine($"set \"TD_PI_PRESET={launch.PresetName}\"");
        sb.AppendLine($"set \"TD_PI_PRESET_DIR={launch.PresetDirectory}\"");
        sb.AppendLine();
        sb.AppendLine($"cd /d \"{launch.ProjectDirectory}\"");
        sb.AppendLine("echo [td-pi] 预设: " + launch.PresetName);
        sb.AppendLine("echo [td-pi] 项目: " + launch.ProjectDirectory);
        if (launch.SystemBlockFile != null)
            sb.AppendLine("echo [td-pi] 系统提示词注入: 已启用(不会被上下文压缩)");
        sb.AppendLine("echo [td-pi] 正在启动 pi ...");
        sb.AppendLine("echo.");
        sb.AppendLine(BuildPiInvocation(piCommand, launch));
        sb.AppendLine();
        sb.AppendLine("echo.");
        sb.AppendLine("echo [td-pi] pi 已退出,窗口保持打开。");
        sb.AppendLine("echo [td-pi] 提示: 再次以相同预设启动可直接运行 td-pi 管理器。");

        File.WriteAllText(batch, sb.ToString(), new UTF8Encoding(false));
        return batch;
    }

    /// <summary>pi 命令行(单行,含开场消息)。</summary>
    public static string BuildPiInvocation(string piCommand, CompiledLaunch launch)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(piCommand).Append('"');
        foreach (var arg in launch.Arguments)
        {
            sb.Append(' ');
            sb.Append(BatchQuote(arg));
        }
        if (!string.IsNullOrWhiteSpace(launch.OpeningMessage))
        {
            sb.Append(' ');
            sb.Append(BatchQuote(launch.OpeningMessage));
        }
        return sb.ToString();
    }

    private static string BatchQuote(string arg)
    {
        // cmd 批处理安全引号:双引号替换为单引号(开场消息等自由文本),路径不含双引号
        var safe = arg.Replace("\"", "'");
        return safe.Contains(' ') || safe.Contains('&') || safe.Contains('^') || safe.Contains('(') || safe.Contains(')')
            ? $"\"{safe}\""
            : safe;
    }

    /// <summary>在新终端窗口中启动。优先 Windows Terminal,回退 cmd。</summary>
    public bool Launch(CompiledLaunch launch, string? piCommand = null)
    {
        piCommand ??= _env.PiCommand;
        if (piCommand == null) throw new InvalidOperationException("未找到 pi 可执行文件。请确认 pi 已安装并在 PATH 中。");
        var batch = WriteBatch(launch, piCommand);

        try
        {
            if (_env.WindowsTerminal != null)
            {
                var wtArgs = $"-d \"{launch.ProjectDirectory}\" cmd /k \"{batch}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = _env.WindowsTerminal,
                    Arguments = wtArgs,
                    UseShellExecute = true,
                });
            }
            else
            {
                var cmdArgs = $"/c start \"td-pi - {launch.PresetName}\" /D \"{launch.ProjectDirectory}\" cmd /k \"{batch}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动失败:{ex.Message}", ex);
        }
    }
}
