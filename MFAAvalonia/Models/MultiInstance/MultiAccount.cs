using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MFAAvalonia.Models.MultiInstance;

/// <summary>
/// 多开账号模型
/// </summary>
public partial class MultiAccount : ObservableObject
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// 账号名称
    /// </summary>
    [ObservableProperty]
    private string _name = "新账号";

    /// <summary>
    /// 是否启用
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// 是否为队长号
    /// </summary>
    [ObservableProperty]
    private bool _isLeader = false;

    /// <summary>
    /// 模拟器类型 (mumu, ldplayer, nox, bluestacks, xyaz)
    /// </summary>
    [ObservableProperty]
    private string _emulatorType = "mumu";

    /// <summary>
    /// 模拟器索引 (0, 1, 2, 3, 4)
    /// </summary>
    [ObservableProperty]
    private int _emulatorIndex = 0;

    /// <summary>
    /// ADB序列号 (自动生成或手动设置)
    /// </summary>
    [ObservableProperty]
    private string _adbSerial = "127.0.0.1:16384";

    /// <summary>
    /// 任务列表
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _taskList = new();

    /// <summary>
    /// 资源类型 (官方混服/官服/九游)
    /// </summary>
    [ObservableProperty]
    private string _resourceType = "官服";

    /// <summary>
    /// 账号状态
    /// </summary>
    [ObservableProperty]
    private AccountStatus _status = AccountStatus.Idle;

    /// <summary>
    /// 进度 (0-100)
    /// </summary>
    [ObservableProperty]
    private int _progress = 0;

    /// <summary>
    /// 当前任务
    /// </summary>
    [ObservableProperty]
    private string? _currentTask;

    /// <summary>
    /// 开始时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _startTime;

    /// <summary>
    /// 结束时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _endTime;

    /// <summary>
    /// 错误信息
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// 日志路径
    /// </summary>
    [ObservableProperty]
    private string _logPath = "";

    /// <summary>
    /// 状态颜色（用于UI显示）
    /// </summary>
    public string StatusColor => Status switch
    {
        AccountStatus.Idle => "#808080",      // 灰色
        AccountStatus.Waiting => "#FFA500",   // 橙色
        AccountStatus.Running => "#4285F4",   // 蓝色
        AccountStatus.Completed => "#34A853", // 绿色
        AccountStatus.Failed => "#EA4335",    // 红色
        AccountStatus.Cancelled => "#FBBC05", // 黄色
        _ => "#808080"
    };

    /// <summary>
    /// 状态文本
    /// </summary>
    public string StatusText => Status switch
    {
        AccountStatus.Idle => "空闲",
        AccountStatus.Waiting => "等待中",
        AccountStatus.Running => "运行中",
        AccountStatus.Completed => "已完成",
        AccountStatus.Failed => "失败",
        AccountStatus.Cancelled => "已取消",
        _ => "未知"
    };

    /// <summary>
    /// 重置账号状态
    /// </summary>
    public void Reset()
    {
        Status = AccountStatus.Idle;
        Progress = 0;
        CurrentTask = null;
        StartTime = null;
        EndTime = null;
        ErrorMessage = null;
    }

    /// <summary>
    /// 复制账号配置（用于创建相似账号）
    /// </summary>
    public MultiAccount Clone()
    {
        return new MultiAccount
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{Name} - 副本",
            IsEnabled = IsEnabled,
            IsLeader = false, // 复制的账号不是队长号
            EmulatorType = EmulatorType,
            EmulatorIndex = EmulatorIndex + 1,
            TaskList = new ObservableCollection<string>(TaskList),
            ResourceType = ResourceType
        };
    }
}

/// <summary>
/// 账号状态枚举
/// </summary>
public enum AccountStatus
{
    /// <summary>
    /// 空闲
    /// </summary>
    Idle,

    /// <summary>
    /// 等待中
    /// </summary>
    Waiting,

    /// <summary>
    /// 运行中
    /// </summary>
    Running,

    /// <summary>
    /// 已完成
    /// </summary>
    Completed,

    /// <summary>
    /// 失败
    /// </summary>
    Failed,

    /// <summary>
    /// 已取消
    /// </summary>
    Cancelled
}
