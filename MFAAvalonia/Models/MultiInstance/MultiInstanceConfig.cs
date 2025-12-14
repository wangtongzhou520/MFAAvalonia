using System.Collections.Generic;

namespace MFAAvalonia.Models.MultiInstance;

/// <summary>
/// 多开配置模型
/// </summary>
public class MultiInstanceConfig
{
    /// <summary>
    /// 执行模式
    /// </summary>
    public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;

    /// <summary>
    /// 最大并行数量
    /// </summary>
    public int MaxParallelCount { get; set; } = 3;

    /// <summary>
    /// 账号列表
    /// </summary>
    public List<MultiAccount> Accounts { get; set; } = new();

    /// <summary>
    /// 自动启动模拟器
    /// </summary>
    public bool AutoStartEmulator { get; set; } = false;

    /// <summary>
    /// 账号间延迟（秒）
    /// </summary>
    public int DelayBetweenAccounts { get; set; } = 5;

    /// <summary>
    /// 出错时停止全部
    /// </summary>
    public bool StopOnError { get; set; } = false;

    /// <summary>
    /// 启用独立日志
    /// </summary>
    public bool EnableSeparateLogs { get; set; } = true;

    /// <summary>
    /// 日志目录
    /// </summary>
    public string LogDirectory { get; set; } = "logs/multi";

    /// <summary>
    /// 配置版本（用于兼容性）
    /// </summary>
    public string Version { get; set; } = "1.0";
}

/// <summary>
/// 执行模式
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// 串行执行
    /// </summary>
    Sequential,

    /// <summary>
    /// 并行执行
    /// </summary>
    Parallel
}
