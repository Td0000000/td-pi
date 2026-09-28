using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using TdPi.App.ViewModels;
using TdPi.App.Views;
using TdPi.Core.Services;

namespace TdPi.App;

public partial class App : Application
{
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static TrayIcon? _tray;
    private static bool _exitRequested;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!SingleInstance.TryAcquire())
            {
                // 已有实例在运行:TryAcquire 已发出唤出信号,本进程直接退出。
                // 注意:不能用 desktop.Shutdown(0) —— 主循环尚未启动时调用会抛
                // "Dispatcher shut down" 未处理异常;Environment.Exit 干净退出。
                Environment.Exit(0);
                return;
            }

            _desktop = desktop;
            var env = new PiEnvironment();
            env.Detect();
            var settings = new AppSettings(env.AppSettingsFile);
            var skillService = new SkillService(env);
            var mcpService = new McpService(env);
            var pluginService = new PluginService(env);
            var cliRunner = new PiCliRunner(env);
            var skills = new SkillsViewModel(env, skillService);
            var mcp = new McpViewModel(env, mcpService, cliRunner);
            var plugins = new PluginsViewModel(env, pluginService, cliRunner);
            var modelSettingsService = new ModelSettingsService(env);
            var models = new ModelsViewModel(env, modelSettingsService);
            var presetService = new PresetService(env);
            var launchService = new LaunchService(env);
            var presets = new PresetsViewModel(env, presetService, cliRunner,
                skillService, mcpService, pluginService);
            var launcher = new PresetLauncherViewModel(env, presetService, launchService);
            var sessions = new SessionsViewModel(env, new SessionService(env), launchService, presetService);
            var diagnostics = new DiagnosticsViewModel(env, new ExtensionDeployer(env), cliRunner);
            var main = new MainViewModel(env, settings, skills, mcp, plugins, models, presets, launcher,
                sessions, diagnostics, presetService);

            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;

            // 关闭窗口 → 隐藏到托盘(真正退出走托盘菜单「退出」)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                if (_exitRequested) return;
                window.Hide();
                e.Cancel = true;
            };

            BuildTrayIcon();
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowMainWindow));
        }
        base.OnFrameworkInitializationCompleted();
    }

    // ---------------- 托盘 ----------------

    /// <summary>托盘图标:像素风 π。左键唤出窗口,右键 = 打开管理器 / 退出。</summary>
    private static void BuildTrayIcon()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;
            var icons = TrayIcon.GetIcons(app);
            if (icons == null)
            {
                icons = new TrayIcons();
                TrayIcon.SetIcons(app, icons);
            }
            foreach (var old in icons) old.Dispose();
            icons.Clear();

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://td-pi/Assets/AppIcon.ico")));
            var tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "td-pi 管理器",
                Menu = BuildTrayMenu(),
                IsVisible = true,
            };
            tray.Clicked += (_, _) => ShowMainWindow();
            _tray = tray;
            icons.Add(tray);
        }
        catch (Exception ex)
        {
            // 托盘不可用时降级(不影响主窗口),但记录原因便于排查
            try
            {
                var log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "td-pi", "tray-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, DateTime.Now + " 托盘初始化失败: " + ex + Environment.NewLine);
            }
            catch { /* 日志失败不再抛出 */ }
        }
    }

    private static NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();

        var open = new NativeMenuItem("打开管理器");
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        menu.Items.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("退出");
        quit.Click += (_, _) =>
        {
            _exitRequested = true;
            _desktop?.Shutdown();
        };
        menu.Items.Add(quit);
        return menu;
    }

    private static void ShowMainWindow()
    {
        var w = _desktop?.MainWindow;
        if (w == null) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }
}
