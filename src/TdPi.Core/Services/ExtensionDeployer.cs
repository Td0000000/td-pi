using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace TdPi.Core.Services;

/// <summary>把内嵌的 td-pi-preset 扩展部署到 ~/.pi/agent/extensions/td-pi-preset/index.ts。</summary>
public class ExtensionDeployer
{
    private const string ResourceName = "td-pi-preset.index.ts";

    private readonly PiEnvironment _env;

    public ExtensionDeployer(PiEnvironment env) => _env = env;

    public string? GetInstalledVersion()
    {
        try
        {
            if (!File.Exists(_env.TdPiExtensionFile)) return null;
            var firstLines = File.ReadLines(_env.TdPiExtensionFile).Take(5).ToList();
            foreach (var line in firstLines)
            {
                var idx = line.IndexOf("td-pi-preset v", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return ExtractVersionToken(line[(idx + "td-pi-preset v".Length)..]);
                }
            }
            return "未知版本";
        }
        catch
        {
            return null;
        }
    }

    public string? GetEmbeddedVersion()
    {
        var content = ReadEmbedded();
        if (content == null) return null;
        foreach (var line in content.Split('\n').Take(5))
        {
            var idx = line.IndexOf("td-pi-preset v", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return ExtractVersionToken(line[(idx + "td-pi-preset v".Length)..]);
            }
        }
        return null;
    }

    /// <summary>取版本号 token:到第一个空白/减号为止(避免带出注释尾文字)。</summary>
    private static string ExtractVersionToken(string rest)
    {
        rest = rest.TrimStart('*', ' ', '\r');
        var end = rest.IndexOfAny(new[] { ' ', '\t', '-', '—' });
        var token = (end >= 0 ? rest[..end] : rest).Trim();
        return token.Length > 0 ? token : "未知版本";
    }

    private string? ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public bool Deploy()
    {
        var content = ReadEmbedded() ?? throw new InvalidOperationException("内嵌扩展资源缺失。");
        Directory.CreateDirectory(_env.TdPiExtensionDir);
        File.WriteAllText(_env.TdPiExtensionFile, content, new UTF8Encoding(false));
        return true;
    }

    public bool IsUpToDate()
    {
        var installed = GetInstalledVersion();
        var embedded = GetEmbeddedVersion();
        return installed != null && embedded != null && installed == embedded;
    }
}

/// <summary>运行 pi CLI 子命令(install/remove/update/config)并捕获输出。</summary>
public class PiCliRunner
{
    private readonly PiEnvironment _env;

    public PiCliRunner(PiEnvironment env) => _env = env;

    public async Task<(int ExitCode, string Output)> RunAsync(
        string arguments, string workDir, CancellationToken ct = default)
    {
        var pi = _env.PiCommand ?? throw new InvalidOperationException("未找到 pi 可执行文件。");
        var psi = new ProcessStartInfo
        {
            FileName = pi,
            Arguments = arguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, sb.ToString());
    }
}
