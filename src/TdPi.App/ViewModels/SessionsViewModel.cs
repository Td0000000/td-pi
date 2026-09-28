using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdPi.Core.Models;
using TdPi.Core.Services;

namespace TdPi.App.ViewModels;

public partial class SessionRowViewModel : ViewModelBase
{
    private readonly SessionsViewModel _owner;

    public SessionRowViewModel(SessionsViewModel owner, SessionInfo session)
    {
        _owner = owner;
        Session = session;
    }

    public SessionInfo Session { get; }

    [ObservableProperty]
    private bool _isChecked;

    public string TimeText => Session.StartedAt == default
        ? "?"
        : Session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ModifiedText => Session.LastModified.ToString("MM-dd HH:mm");
    public string Name => string.IsNullOrWhiteSpace(Session.Name) ? "(未命名)" : Session.Name;
    public bool HasName => !string.IsNullOrWhiteSpace(Session.Name);
    public string Project => string.IsNullOrWhiteSpace(Session.Cwd) ? "(未知)" : Session.Cwd;
    public string Preview => string.IsNullOrWhiteSpace(Session.FirstUserMessage) ? "(无对话内容)" : Session.FirstUserMessage;
    public bool HasPreview => !string.IsNullOrWhiteSpace(Session.FirstUserMessage);
    public string Model => string.IsNullOrWhiteSpace(Session.Model) ? "-" : Session.Model;
    public string SizeText => SessionService.FormatSize(Session.SizeBytes);
    public string MsgCountText => Session.MessageCount.ToString();

    public bool UsedPreset => Session.UsedPreset;

    /// <summary>预设徽章文字:有预设显示名字,无预设显示「正常启动」。</summary>
    public string PresetText => Session.UsedPreset ? $"预设:{Session.PresetName}" : "正常启动";

    public string PresetTooltip => Session.UsedPreset
        ? $"{Session.PresetOriginText}\n{Session.PresetSourceText}\n系统消息 {Session.PresetSystemMessages} 条 · 上下文消息 {Session.PresetContextMessages} 条"
        : "该会话未检测到任何预设注入记录";

    [RelayCommand]
    private async Task ViewAsync() => await _owner.ViewRowAsync(this);

    [RelayCommand]
    private async Task ResumeAsync() => await _owner.ResumeRowAsync(this);

    [RelayCommand]
    private async Task DeleteAsync() => await _owner.DeleteRowsAsync([this]);

    [RelayCommand]
    private void OpenLocation()
    {
        if (!File.Exists(Session.File)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{Session.File}\"",
            UseShellExecute = true,
        });
    }
}

public partial class SessionsViewModel : ViewModelBase
{
    private readonly PiEnvironment _env;
    private readonly SessionService _service;
    private readonly LaunchService _launcher;
    private readonly PresetService _presets;

    public MainViewModel? Main { get; set; }

    /// <summary>当前筛选下展示的行(与 _all 共享行实例,勾选状态在刷新/筛选间保留)。</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    private List<SessionRowViewModel> _all = new();

    [ObservableProperty]
    private int _filterMode; // 0=全部 1=使用预设 2=正常启动

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _rootHint = "";

    [ObservableProperty]
    private SessionRowViewModel? _selected;

    [ObservableProperty]
    private bool _allChecked;

    [ObservableProperty]
    private int _checkedCount;

    [ObservableProperty]
    private bool _busy;

    partial void OnFilterModeChanged(int value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public SessionsViewModel(PiEnvironment env, SessionService service, LaunchService launcher, PresetService presets)
    {
        _env = env;
        _service = service;
        _launcher = launcher;
        _presets = presets;
    }

    /// <summary>扫描全部会话文件(后台线程,避免大量会话时卡 UI)。全量异常保护(async void 不得抛出)。</summary>
    public async void Refresh()
    {
        if (Busy)
        {
            Main?.SetStatus("正在扫描会话,请稍候…");
            return;
        }
        Busy = true;
        try
        {
            RootHint = _env.SessionsRoot;
            List<SessionInfo> list;
            try
            {
                list = await Task.Run(_service.ListSessions);
            }
            catch (Exception ex)
            {
                list = new List<SessionInfo>();
                SummaryText = $"扫描失败:{ex.Message}";
            }

            // 保留用户在旧列表中的勾选(按会话文件路径)
            var checkedFiles = new HashSet<string>(
                _all.Where(r => r.IsChecked).Select(r => r.Session.File),
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in _all)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
            _all = list.Select(s => new SessionRowViewModel(this, s)).ToList();
            foreach (var row in _all)
            {
                row.PropertyChanged += OnRowPropertyChanged;
                if (checkedFiles.Contains(row.Session.File)) row.IsChecked = true;
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            SummaryText = $"刷新失败:{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionRowViewModel.IsChecked))
        {
            UpdateCheckedState();
        }
    }

    /// <summary>应用筛选(共享行实例,勾选状态不丢)。</summary>
    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        Sessions.Clear();
        var presets = 0;
        var normals = 0;
        long totalSize = 0;
        foreach (var row in _all)
        {
            if (row.Session.UsedPreset) presets++;
            else normals++;
            totalSize += row.Session.SizeBytes;

            // 过滤:类型
            if (FilterMode == 1 && !row.Session.UsedPreset) continue;
            if (FilterMode == 2 && row.Session.UsedPreset) continue;
            // 过滤:关键字(会话名 / 项目 / 首条消息 / 预设名 / 模型)
            if (keyword.Length > 0 && !MatchesKeyword(row, keyword)) continue;
            Sessions.Add(row);
        }

        SummaryText = $"共 {_all.Count} 个会话 · 使用预设 {presets} · 正常启动 {normals}"
                      + $" · 总大小 {SessionService.FormatSize(totalSize)}"
                      + (Sessions.Count != _all.Count ? $" · 当前筛选显示 {Sessions.Count}" : "");

        // 同步勾选计数/全选框(行实例共享,筛选后可见集合变了,计数必须重算)
        UpdateCheckedState();
    }

    private static bool MatchesKeyword(SessionRowViewModel row, string keyword)
    {
        var s = row.Session;
        return s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || s.Cwd.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || s.FirstUserMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || s.PresetName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || s.Model.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCheckedState()
    {
        var visible = Sessions.ToList();
        CheckedCount = visible.Count(r => r.IsChecked);
        AllChecked = visible.Count > 0 && visible.All(r => r.IsChecked);
    }

    /// <summary>表头全选/取消(作用于当前筛选显示的行)。</summary>
    partial void OnAllCheckedChanged(bool value)
    {
        foreach (var row in Sessions)
        {
            row.IsChecked = value;
        }
        CheckedCount = Sessions.Count(r => r.IsChecked);
    }

    [RelayCommand]
    private void RefreshCmd() => Refresh();

    [RelayCommand]
    private async Task DeleteCheckedAsync()
    {
        var rows = Sessions.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0)
        {
            await Dialogs.InfoAsync("删除会话", "请先勾选要删除的会话(表格第一列)。");
            return;
        }
        await DeleteRowsAsync(rows);
    }

    /// <summary>删除指定行:确认 → 删除文件 → 刷新 → 报告结果。</summary>
    public async Task DeleteRowsAsync(IReadOnlyList<SessionRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        long size = rows.Sum(r => r.Session.SizeBytes);
        var preview = string.Join("\n", rows.Take(5).Select(r =>
            $"  · {(string.IsNullOrWhiteSpace(r.Session.Name) ? "(未命名)" : r.Session.Name)}"
            + $"  {r.TimeText}  {r.Project}"));
        if (rows.Count > 5) preview += $"\n  · …等共 {rows.Count} 个";

        var ok = await Dialogs.ConfirmAsync("删除会话记录",
            $"确定删除 {rows.Count} 个会话记录?\n共 {SessionService.FormatSize(size)}\n\n{preview}\n\n"
            + "删除后这些对话将无法用 pi --continue 找回,且不可恢复。");
        if (!ok) return;

        var (deleted, errors) = _service.Delete(rows.Select(r => r.Session.File));
        Refresh();
        if (errors.Count > 0)
        {
            await Dialogs.InfoAsync("部分会话删除失败",
                $"成功删除 {deleted} 个,失败 {errors.Count} 个:\n\n{string.Join("\n", errors)}\n\n"
                + "失败的会话可能正被运行中的 pi 窗口占用,关闭对应窗口后重试。");
        }
        else
        {
            Main?.SetStatus($"已删除 {deleted} 个会话记录");
        }
    }

    /// <summary>查看会话内容(只读对话文本)。</summary>
    public async Task ViewRowAsync(SessionRowViewModel row)
    {
        var text = await Task.Run(() => _service.BuildTranscript(row.Session));
        var title = "会话记录:" + (string.IsNullOrWhiteSpace(row.Session.Name) ? "(未命名)" : row.Session.Name);
        await Dialogs.ViewTextAsync(title, text);
    }

    /// <summary>
    /// 恢复会话:在新终端窗口中 cd 到会话项目目录,pi --session &lt;文件&gt;。
    /// 会话用过预设时自动恢复预设注入(预设已删除则照常启动,不注入)。
    /// </summary>
    public async Task ResumeRowAsync(SessionRowViewModel row)
    {
        var s = row.Session;
        if (!File.Exists(s.File))
        {
            await Dialogs.InfoAsync("恢复会话", "会话文件已不存在(可能已被删除)。刷新后重试。");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.Cwd) || !Directory.Exists(s.Cwd))
        {
            await Dialogs.InfoAsync("无法恢复",
                $"会话的项目目录不存在:\n{s.Cwd}\n\n(项目被移动/删除后无法原地恢复;可手动在原目录用 pi --session 恢复。)");
            return;
        }
        try
        {
            PresetInfo? preset = null;
            if (s.UsedPreset)
            {
                preset = _presets.Find(s.PresetName, s.Cwd);
                if (preset == null)
                {
                    var go = await Dialogs.ConfirmAsync("预设已不存在",
                        $"该会话使用了预设 {s.PresetName},但预设已被删除/找不到。\n仍要恢复会话吗?(恢复后无预设注入)");
                    if (!go) return;
                }
            }
            var compiled = _launcher.BuildResumeLaunch(s, preset);
            _launcher.Launch(compiled);
            Main?.SetStatus($"已在新窗口恢复会话:{(string.IsNullOrWhiteSpace(s.Name) ? "(未命名)" : s.Name)}");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("恢复失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLocation()
    {
        var row = Selected ?? Sessions.FirstOrDefault();
        row?.OpenLocationCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ViewSelectedAsync()
    {
        if (Selected != null) await ViewRowAsync(Selected);
    }

    [RelayCommand]
    private async Task ResumeSelectedAsync()
    {
        if (Selected != null) await ResumeRowAsync(Selected);
    }
}
