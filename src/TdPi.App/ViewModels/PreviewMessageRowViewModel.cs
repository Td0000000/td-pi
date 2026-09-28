using CommunityToolkit.Mvvm.ComponentModel;

namespace TdPi.App.ViewModels;

/// <summary>上下文预览行:角色 + 内容 + 层级说明。</summary>
public partial class PreviewMessageRowViewModel : ViewModelBase
{
    public required string Role { get; init; }
    public required string RoleDisplay { get; init; }
    public required string Content { get; init; }
    public required string Note { get; init; }

    public bool IsSystem => Role == "system";
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsReal => Role == "real";
    public bool IsMarker => Role == "marker";
}
