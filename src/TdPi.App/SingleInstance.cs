using System.Threading;

namespace TdPi.App;

/// <summary>
/// 单实例守卫:第二个 td-pi 实例启动时不重复开窗口,只唤醒已运行实例的主窗口
/// (配合「关闭窗口隐藏到托盘」—— 用户再双击 td-pi.exe 时能找回窗口)。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\td-pi-instance";
    private const string EventName = "Local\\td-pi-activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;

    /// <summary>尝试成为主实例。返回 false = 已有实例在运行(已通知其显示窗口),调用方应直接退出。</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var isFirst);
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out _);
            if (isFirst) return true;
            _activateEvent.Set();
            return false;
        }
        catch
        {
            return true; // 守卫本身异常时不阻塞正常启动
        }
    }

    /// <summary>监听唤醒信号(线程池),通过 UI 线程回调。</summary>
    public static void Listen(Action onActivate)
    {
        var evt = _activateEvent;
        if (evt == null) return;
        Task.Run(() =>
        {
            while (evt.WaitOne())
            {
                try { onActivate(); }
                catch { /* 窗口已释放等场景忽略 */ }
            }
        });
    }
}
