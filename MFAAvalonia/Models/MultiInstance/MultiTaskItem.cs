using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Models.MultiInstance;

/// <summary>
/// 多实例任务项模型（独立于主任务系统）
/// </summary>
public partial class MultiTaskItem : ObservableObject
{
    /// <summary>
    /// 任务唯一标识符
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 任务显示名称（已本地化）
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// 任务入口（pipeline任务名）
    /// </summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>
    /// 任务描述
    /// </summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>
    /// 是否勾选此任务
    /// </summary>
    [ObservableProperty]
    private bool _isChecked = false;

    /// <summary>
    /// 任务类型（单人/组队）
    /// </summary>
    [ObservableProperty]
    private MultiTaskType _taskType = MultiTaskType.Solo;

    /// <summary>
    /// 解析后的图标路径（用于UI绑定）
    /// </summary>
    [ObservableProperty]
    private string? _resolvedIcon;

    /// <summary>
    /// 是否有图标
    /// </summary>
    [ObservableProperty]
    private bool _hasIcon;

    /// <summary>
    /// 引用原始的 MaaInterfaceTask（用于执行时传递给 MaaFramework）
    /// </summary>
    public MaaInterface.MaaInterfaceTask? InterfaceTask { get; set; }

    /// <summary>
    /// 用户配置的 Option 选项（与主页面的 DragItemViewModel.InterfaceItem.Option 对应）
    /// 简化版：直接使用 InterfaceTask 中的默认值
    /// </summary>
    public List<MaaInterface.MaaInterfaceSelectOption>? Option { get; set; }

    /// <summary>
    /// 用户配置的 Advanced 选项
    /// </summary>
    public List<MaaInterface.MaaInterfaceSelectAdvanced>? Advanced { get; set; }

    /// <summary>
    /// 重复执行次数
    /// </summary>
    [ObservableProperty]
    private int _repeatCount = 1;

    /// <summary>
    /// 是否可重复执行
    /// </summary>
    public bool Repeatable => InterfaceTask?.Repeatable ?? false;

    /// <summary>
    /// 从 MaaInterfaceTask 创建 MultiTaskItem
    /// </summary>
    public static MultiTaskItem FromInterfaceTask(MaaInterface.MaaInterfaceTask interfaceTask)
    {
        var item = new MultiTaskItem
        {
            Name = interfaceTask.Name ?? string.Empty,
            DisplayName = LanguageHelper.GetLocalizedDisplayName(
                interfaceTask.Label ?? interfaceTask.DisplayName,
                interfaceTask.Name ?? string.Empty),
            Entry = interfaceTask.Entry ?? string.Empty,
            Description = interfaceTask.Description,
            IsChecked = interfaceTask.Check ?? false,
            TaskType = MultiTaskType.Solo, // 默认都是单人任务，后续用户会标记组队任务
            ResolvedIcon = interfaceTask.ResolvedIcon,
            HasIcon = interfaceTask.HasIcon,
            InterfaceTask = interfaceTask,
            // 简化版：直接复制 InterfaceTask 中的默认 Option/Advanced 配置
            Option = interfaceTask.Option != null
                ? new List<MaaInterface.MaaInterfaceSelectOption>(interfaceTask.Option)
                : null,
            Advanced = interfaceTask.Advanced != null
                ? new List<MaaInterface.MaaInterfaceSelectAdvanced>(interfaceTask.Advanced)
                : null,
            RepeatCount = interfaceTask.RepeatCount ?? 1
        };

        return item;
    }

    /// <summary>
    /// 克隆任务项
    /// </summary>
    public MultiTaskItem Clone()
    {
        return new MultiTaskItem
        {
            Name = this.Name,
            DisplayName = this.DisplayName,
            Entry = this.Entry,
            Description = this.Description,
            IsChecked = this.IsChecked,
            TaskType = this.TaskType,
            ResolvedIcon = this.ResolvedIcon,
            HasIcon = this.HasIcon,
            InterfaceTask = this.InterfaceTask,
            // 克隆 Option/Advanced 配置
            Option = this.Option != null
                ? new List<MaaInterface.MaaInterfaceSelectOption>(this.Option)
                : null,
            Advanced = this.Advanced != null
                ? new List<MaaInterface.MaaInterfaceSelectAdvanced>(this.Advanced)
                : null,
            RepeatCount = this.RepeatCount
        };
    }
}

/// <summary>
/// 任务类型枚举
/// </summary>
public enum MultiTaskType
{
    /// <summary>
    /// 单人任务（所有启用的账号并行执行）
    /// </summary>
    Solo,

    /// <summary>
    /// 组队任务（仅队长号执行）
    /// </summary>
    Team
}
