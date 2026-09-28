using CommunityToolkit.Mvvm.ComponentModel;

namespace TdPi.App.ViewModels;

/// <summary>暴露视图行:名称 + 详情 + 状态徽章(on/off/info/warn)。</summary>
public partial class ExposureItemViewModel : ViewModelBase
{
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public required string StatusText { get; init; }

    /// <summary>on | off | info | warn</summary>
    public required string StatusKind { get; init; }

    public bool StatusOn => StatusKind == "on";
    public bool StatusOff => StatusKind == "off";
    public bool StatusInfo => StatusKind == "info";
    public bool StatusWarn => StatusKind == "warn";
}
