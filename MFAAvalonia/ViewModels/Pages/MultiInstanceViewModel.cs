using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.Models.MultiInstance;
using MFAAvalonia.Services;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 多开管理页面ViewModel
/// </summary>
public partial class MultiInstanceViewModel : ViewModelBase
{
    #region 属性

    /// <summary>
    /// 账号列表
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<MultiAccount> _accounts = new();

    /// <summary>
    /// 选中的账号
    /// </summary>
    [ObservableProperty]
    private MultiAccount? _selectedAccount;

    /// <summary>
    /// 是否正在运行
    /// </summary>
    [ObservableProperty]
    private bool _isRunning = false;

    /// <summary>
    /// 自动启动模拟器
    /// </summary>
    [ObservableProperty]
    private bool _autoStartEmulator = false;

    /// <summary>
    /// 出错时停止全部
    /// </summary>
    [ObservableProperty]
    private bool _stopOnError = false;

    /// <summary>
    /// 最大并发数
    /// </summary>
    [ObservableProperty]
    private int _maxConcurrency = 5;

    /// <summary>
    /// 调度器实例
    /// </summary>
    private MultiInstanceScheduler? _scheduler;

    /// <summary>
    /// 取消令牌源
    /// </summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// 总进度文本
    /// </summary>
    [ObservableProperty]
    private string _progressText = "0/0";

    /// <summary>
    /// 启用的账号数量
    /// </summary>
    public int EnabledAccountCount => Accounts.Count(a => a.IsEnabled);

    /// <summary>
    /// 队长号名称
    /// </summary>
    public string LeaderAccountName => Accounts.FirstOrDefault(a => a.IsLeader)?.Name ?? "未设置";

    /// <summary>
    /// 单人任务列表（所有账号并行执行）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<MultiTaskItem> _soloTasks = new();

    /// <summary>
    /// 组队任务列表（仅队长号执行）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<MultiTaskItem> _teamTasks = new();

    #endregion

    #region 构造函数

    public MultiInstanceViewModel()
    {
        // 加载实际的ADB设备列表
        LoadAdbDevices();

        // 加载任务列表
        LoadTasks();
    }

    #endregion

    #region 命令

    /// <summary>
    /// 添加账号命令
    /// </summary>
    [RelayCommand]
    private void AddAccount()
    {
        var newAccount = new MultiAccount
        {
            Name = $"账号{Accounts.Count + 1}",
            EmulatorIndex = Accounts.Count,
            EmulatorType = "mumu"
        };

        Accounts.Add(newAccount);
        SelectedAccount = newAccount;
    }

    /// <summary>
    /// 删除账号命令
    /// </summary>
    [RelayCommand]
    private void RemoveAccount()
    {
        if (SelectedAccount == null) return;

        var wasLeader = SelectedAccount.IsLeader;
        var index = Accounts.IndexOf(SelectedAccount);
        Accounts.Remove(SelectedAccount);

        // 如果删除的是队长号，将第一个账号设为队长
        if (wasLeader && Accounts.Count > 0)
        {
            Accounts[0].IsLeader = true;
            OnPropertyChanged(nameof(LeaderAccountName));
        }

        // 选中下一个账号
        if (Accounts.Count > 0)
        {
            SelectedAccount = Accounts[Math.Min(index, Accounts.Count - 1)];
        }
    }

    /// <summary>
    /// 复制账号命令
    /// </summary>
    [RelayCommand]
    private void DuplicateAccount()
    {
        if (SelectedAccount == null) return;

        var clonedAccount = SelectedAccount.Clone();
        Accounts.Add(clonedAccount);
        SelectedAccount = clonedAccount;
    }

    /// <summary>
    /// 全选账号命令
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var account in Accounts)
        {
            account.IsEnabled = true;
        }
    }

    /// <summary>
    /// 反选账号命令
    /// </summary>
    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var account in Accounts)
        {
            account.IsEnabled = !account.IsEnabled;
        }
    }

    /// <summary>
    /// 开始全部任务命令
    /// </summary>
    [RelayCommand]
    private async Task StartAll()
    {
        LoggerHelper.Info("[MultiInstance] ========== StartAll 被调用 ==========");

        if (IsRunning)
        {
            LoggerHelper.Warning("[MultiInstance] 任务正在运行中，跳过");
            return;
        }

        IsRunning = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            // 获取启用的账号
            var enabledAccounts = Accounts.Where(a => a.IsEnabled).ToList();
            LoggerHelper.Info($"[MultiInstance] 启用的账号数量: {enabledAccounts.Count}");

            // 输出每个启用账号的详细信息
            for (int i = 0; i < enabledAccounts.Count; i++)
            {
                var acc = enabledAccounts[i];
                LoggerHelper.Info($"[MultiInstance]   账号[{i}]: Name={acc.Name}, AdbSerial={acc.AdbSerial}, IsLeader={acc.IsLeader}");
            }

            if (!enabledAccounts.Any())
            {
                LoggerHelper.Warning("[MultiInstance] 没有启用的账号");
                return;
            }

            // 获取勾选的单人任务和组队任务
            var selectedSoloTasks = SoloTasks.Where(t => t.IsChecked).ToList();
            var selectedTeamTasks = TeamTasks.Where(t => t.IsChecked).ToList();

            LoggerHelper.Info($"[MultiInstance] 选中的单人任务: {selectedSoloTasks.Count}");
            LoggerHelper.Info($"[MultiInstance] 选中的组队任务: {selectedTeamTasks.Count}");

            if (!selectedSoloTasks.Any() && !selectedTeamTasks.Any())
            {
                LoggerHelper.Warning("[MultiInstance] 没有勾选任何任务");
                return;
            }

            LoggerHelper.Info($"[MultiInstance] 开始执行: {enabledAccounts.Count} 个账号, {selectedSoloTasks.Count} 个单人任务, {selectedTeamTasks.Count} 个组队任务");

            // 第一阶段：所有账号并行执行单人任务
            if (selectedSoloTasks.Any())
            {
                LoggerHelper.Info($"[MultiInstance] ========== 第一阶段：并行执行单人任务 ==========");

                // 创建调度器
                _scheduler = new MultiInstanceScheduler(MaxConcurrency);

                // 为每个启用的账号（包括队长）创建工作项
                foreach (var account in enabledAccounts)
                {
                    account.Status = AccountStatus.Waiting;
                    var workItem = new WorkItem
                    {
                        Account = account,
                        Tasks = selectedSoloTasks.Select(t => t.Clone()).ToList(),
                        TaskType = "单人任务"
                    };
                    _scheduler.EnqueueTask(workItem);
                }

                // 执行所有任务
                await _scheduler.ProcessAllAsync();

                // 释放调度器
                _scheduler.Dispose();
                _scheduler = null;

                LoggerHelper.Info($"[MultiInstance] 所有账号的单人任务执行完成");
            }

            // 第二阶段：队长号执行组队任务
            if (selectedTeamTasks.Any())
            {
                var leaderAccount = enabledAccounts.FirstOrDefault(a => a.IsLeader);
                if (leaderAccount != null)
                {
                    var leaderDisplayName = GetAccountDisplayName(leaderAccount);
                    LoggerHelper.Info($"[MultiInstance] ========== 第二阶段：队长号执行组队任务 ==========");
                    LoggerHelper.Info($"[MultiInstance] 队长号 [{leaderDisplayName}] 将执行 {selectedTeamTasks.Count} 个组队任务");

                    // 创建调度器（队长独享，并发数为1）
                    _scheduler = new MultiInstanceScheduler(1);

                    leaderAccount.Status = AccountStatus.Waiting;
                    var workItem = new WorkItem
                    {
                        Account = leaderAccount,
                        Tasks = selectedTeamTasks.Select(t => t.Clone()).ToList(),
                        TaskType = "组队任务"
                    };
                    _scheduler.EnqueueTask(workItem);

                    await _scheduler.ProcessAllAsync();

                    _scheduler.Dispose();
                    _scheduler = null;

                    LoggerHelper.Info($"[MultiInstance] 队长号组队任务执行完成");
                }
                else
                {
                    LoggerHelper.Warning($"[MultiInstance] 没有队长号，跳过组队任务");
                }
            }

            UpdateProgressText();
            LoggerHelper.Info("[MultiInstance] ========== 全部任务执行完成 ==========");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[MultiInstance] 执行任务失败: {ex.Message}");
            LoggerHelper.Error($"[MultiInstance] 异常堆栈: {ex.StackTrace}");
        }
        finally
        {
            IsRunning = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _scheduler?.Dispose();
            _scheduler = null;
        }
    }

    /// <summary>
    /// 停止全部任务命令
    /// </summary>
    [RelayCommand]
    private void StopAll()
    {
        if (!IsRunning) return;

        LoggerHelper.Info("[MultiInstance] ========== 用户请求停止所有任务 ==========");

        // 取消令牌
        _cancellationTokenSource?.Cancel();

        // 停止调度器
        _scheduler?.Stop();

        // 更新账号状态
        foreach (var account in Accounts)
        {
            if (account.Status == AccountStatus.Running || account.Status == AccountStatus.Waiting)
            {
                account.Status = AccountStatus.Cancelled;
            }
        }

        IsRunning = false;
        LoggerHelper.Info("[MultiInstance] 所有任务已停止");
    }

    /// <summary>
    /// 重置全部账号命令
    /// </summary>
    [RelayCommand]
    private void ResetAll()
    {
        foreach (var account in Accounts)
        {
            account.Reset();
        }

        UpdateProgressText();
    }

    /// <summary>
    /// 保存配置命令
    /// </summary>
    [RelayCommand]
    private void SaveConfig()
    {
        // TODO: 实现保存配置
        // var config = new MultiInstanceConfig
        // {
        //     Mode = ExecutionMode,
        //     MaxParallelCount = MaxParallelCount,
        //     Accounts = Accounts.ToList(),
        //     AutoStartEmulator = AutoStartEmulator,
        //     StopOnError = StopOnError
        // };
        // MultiInstanceConfigManager.SaveConfig(config);
    }

    /// <summary>
    /// 加载配置命令
    /// </summary>
    [RelayCommand]
    private void LoadConfig()
    {
        // TODO: 实现加载配置
        // var config = MultiInstanceConfigManager.LoadConfig();
        // if (config != null)
        // {
        //     ExecutionMode = config.Mode;
        //     MaxParallelCount = config.MaxParallelCount;
        //     AutoStartEmulator = config.AutoStartEmulator;
        //     StopOnError = config.StopOnError;
        //     Accounts = new ObservableCollection<MultiAccount>(config.Accounts);
        // }
    }

    /// <summary>
    /// 刷新设备列表命令
    /// </summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        LoadAdbDevices();
    }

    /// <summary>
    /// 设置队长号命令
    /// </summary>
    [RelayCommand]
    private void SetLeader(MultiAccount account)
    {
        if (account == null) return;

        // 取消所有其他账号的队长状态
        foreach (var acc in Accounts)
        {
            acc.IsLeader = false;
        }

        // 设置选中的账号为队长
        account.IsLeader = true;

        // 通知UI更新队长号名称
        OnPropertyChanged(nameof(LeaderAccountName));
    }

    /// <summary>
    /// 全选单人任务命令
    /// </summary>
    [RelayCommand]
    private void SelectAllSoloTasks()
    {
        foreach (var task in SoloTasks)
        {
            task.IsChecked = true;
        }
    }

    /// <summary>
    /// 取消全选单人任务命令
    /// </summary>
    [RelayCommand]
    private void DeselectAllSoloTasks()
    {
        foreach (var task in SoloTasks)
        {
            task.IsChecked = false;
        }
    }

    /// <summary>
    /// 全选组队任务命令
    /// </summary>
    [RelayCommand]
    private void SelectAllTeamTasks()
    {
        foreach (var task in TeamTasks)
        {
            task.IsChecked = true;
        }
    }

    /// <summary>
    /// 取消全选组队任务命令
    /// </summary>
    [RelayCommand]
    private void DeselectAllTeamTasks()
    {
        foreach (var task in TeamTasks)
        {
            task.IsChecked = false;
        }
    }

    #endregion

    #region 私有方法

    /// <summary>
    /// 获取账号的显示名称（包含端口号以便区分）
    /// </summary>
    private string GetAccountDisplayName(MultiAccount account)
    {
        if (string.IsNullOrEmpty(account.AdbSerial))
            return account.Name;

        // 提取端口号
        var parts = account.AdbSerial.Split(':');
        var port = parts.Length > 1 ? parts[1] : account.AdbSerial;

        return $"{account.Name}:{port}";
    }

    /// <summary>
    /// 加载ADB设备列表
    /// </summary>
    private void LoadAdbDevices()
    {
        try
        {
            LoggerHelper.Info("========== 开始加载ADB设备列表 ==========");
            Accounts.Clear();

            // 首先检查当前已连接的设备
            var currentDevice = MaaProcessor.Config.AdbDevice.Info;
            LoggerHelper.Info($"MaaProcessor.Config.AdbDevice.Info: {(currentDevice != null ? "不为null" : "为null")}");
            if (currentDevice != null)
            {
                LoggerHelper.Info($"  - Name: {currentDevice.Name}");
                LoggerHelper.Info($"  - AdbSerial: {currentDevice.AdbSerial}");
                LoggerHelper.Info($"  - AdbPath: {currentDevice.AdbPath}");
            }

            // 然后检测所有可用的ADB设备
            LoggerHelper.Info("调用 MaaProcessor.Toolkit.AdbDevice.Find()...");
            var devices = MaaProcessor.Toolkit.AdbDevice.Find();
            LoggerHelper.Info($"Find() 返回结果: {(devices != null ? $"不为null, 数量={devices.Count}" : "为null")}");

            if (devices != null && devices.Count > 0)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    LoggerHelper.Info($"  设备[{i}]: Name={devices[i].Name}, Serial={devices[i].AdbSerial}");
                }
            }

            // 如果当前有已连接的设备，优先使用它
            if (currentDevice != null)
            {
                LoggerHelper.Info($"添加当前已连接设备: {currentDevice.Name} ({currentDevice.AdbSerial})");

                // 将当前连接的设备添加到列表首位
                var account = new MultiAccount
                {
                    Name = currentDevice.Name ?? "账号1",
                    EmulatorType = ExtractEmulatorType(currentDevice.Name),
                    EmulatorIndex = 0,
                    AdbSerial = currentDevice.AdbSerial ?? "",
                    ResourceType = "官服",
                    IsEnabled = true
                };

                // 添加示例任务
                account.TaskList.Add("福利签到");
                account.TaskList.Add("帮派签到");

                Accounts.Add(account);
                LoggerHelper.Info($"已添加到Accounts列表，当前数量: {Accounts.Count}");
            }

            // 然后添加其他检测到的设备（排除已连接的）
            if (devices != null && devices.Count > 0)
            {
                LoggerHelper.Info("开始添加其他检测到的设备...");
                foreach (var device in devices)
                {
                    // 跳过已经添加的当前设备（使用更严格的字符串比较）
                    if (currentDevice != null &&
                        !string.IsNullOrEmpty(device.AdbSerial) &&
                        !string.IsNullOrEmpty(currentDevice.AdbSerial) &&
                        string.Equals(device.AdbSerial, currentDevice.AdbSerial, StringComparison.OrdinalIgnoreCase))
                    {
                        LoggerHelper.Info($"  跳过重复设备（与当前设备相同）: {device.Name} (Serial: {device.AdbSerial})");
                        continue;
                    }

                    // 检查是否已经添加过此设备（去重）
                    if (Accounts.Any(a => string.Equals(a.AdbSerial, device.AdbSerial, StringComparison.OrdinalIgnoreCase)))
                    {
                        LoggerHelper.Info($"  跳过重复设备（已在列表中）: {device.Name} (Serial: {device.AdbSerial})");
                        continue;
                    }

                    // 最多添加5个设备
                    if (Accounts.Count >= 5)
                    {
                        LoggerHelper.Info("  已达到最大数量5个，停止添加");
                        break;
                    }

                    var account = new MultiAccount
                    {
                        Name = device.Name ?? $"账号{Accounts.Count + 1}",
                        EmulatorType = ExtractEmulatorType(device.Name),
                        EmulatorIndex = Accounts.Count,
                        AdbSerial = device.AdbSerial ?? "",
                        ResourceType = "官服",
                        IsEnabled = Accounts.Count < 3 // 默认启用前3个
                    };

                    // 添加示例任务
                    account.TaskList.Add("福利签到");
                    account.TaskList.Add("帮派签到");

                    Accounts.Add(account);
                    LoggerHelper.Info($"  添加设备: {account.Name} (Serial: {account.AdbSerial}), 当前总数: {Accounts.Count}");
                }
            }

            // 如果还是没有设备，使用示例数据
            if (Accounts.Count == 0)
            {
                LoggerHelper.Info("未检测到任何设备，使用示例数据");
                InitializeSampleData();
                return;
            }

            // 默认选中第一个账号
            if (Accounts.Count > 0)
            {
                SelectedAccount = Accounts[0];
                // 默认第一个账号为队长号
                Accounts[0].IsLeader = true;
                LoggerHelper.Info($"选中第一个账号: {SelectedAccount.Name}，并设置为队长号");
            }

            UpdateProgressText();
            LoggerHelper.Info($"========== 加载完成，共 {Accounts.Count} 个设备 ==========");
        }
        catch (Exception ex)
        {
            // 如果加载失败，使用示例数据
            LoggerHelper.Error($"加载ADB设备失败: {ex.Message}");
            LoggerHelper.Error($"异常堆栈: {ex.StackTrace}");
            InitializeSampleData();
        }
    }

    /// <summary>
    /// 从设备名称提取模拟器类型
    /// </summary>
    private string ExtractEmulatorType(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return "未知";

        var lowerName = deviceName.ToLower();
        if (lowerName.Contains("mumu") || lowerName.Contains("网易"))
            return "MuMu";
        if (lowerName.Contains("ld") || lowerName.Contains("雷电"))
            return "雷电";
        if (lowerName.Contains("nox") || lowerName.Contains("夜神"))
            return "夜神";
        if (lowerName.Contains("bluestacks") || lowerName.Contains("蓝叠"))
            return "蓝叠";
        if (lowerName.Contains("memu") || lowerName.Contains("逍遥"))
            return "逍遥";

        return "其他";
    }

    /// <summary>
    /// 加载任务列表
    /// </summary>
    private void LoadTasks()
    {
        try
        {
            LoggerHelper.Info("[MultiInstance] 开始加载任务列表");

            // 从 MultiInstanceTaskService 加载所有任务
            var allTasks = MultiInstanceTaskService.LoadTasks();

            // 标记组队任务（精确匹配）
            MultiInstanceTaskService.MarkAsTeamTasks(allTasks,
                "创建队伍",
                "副本",
                "创建队伍2",
                "捉鬼任务",
                "捉鬼任务-无限/手动选择执行几轮"
            );

            // 标记组队任务（前缀匹配）
            MultiInstanceTaskService.MarkAsTeamTasksByPrefix(allTasks,
                "副本320",
                "副本520",
                "捉鬼"
            );

            // 分类为单人任务和组队任务
            var (soloTasks, teamTasks) = MultiInstanceTaskService.ClassifyTasks(allTasks);

            // 更新任务列表
            SoloTasks.Clear();
            TeamTasks.Clear();

            foreach (var task in soloTasks)
            {
                SoloTasks.Add(task);
            }

            foreach (var task in teamTasks)
            {
                TeamTasks.Add(task);
            }

            LoggerHelper.Info($"[MultiInstance] 任务加载完成: 单人任务 {SoloTasks.Count} 个, 组队任务 {TeamTasks.Count} 个");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[MultiInstance] 加载任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 初始化示例数据（仅用于开发测试）
    /// </summary>
    private void InitializeSampleData()
    {
        // 创建5个示例账号
        for (int i = 0; i < 5; i++)
        {
            var account = new MultiAccount
            {
                Name = $"账号{i + 1}",
                EmulatorType = "mumu",
                EmulatorIndex = i,
                AdbSerial = $"127.0.0.1:{16384 + i}",
                ResourceType = "官服",
                IsEnabled = i < 3 // 默认启用前3个
            };

            // 添加示例任务
            account.TaskList.Add("福利签到");
            account.TaskList.Add("帮派签到");
            if (i < 2)
            {
                account.TaskList.Add("副本520(89/115)");
            }

            Accounts.Add(account);
        }

        // 默认选中第一个账号
        if (Accounts.Count > 0)
        {
            SelectedAccount = Accounts[0];
            // 默认第一个账号为队长号
            Accounts[0].IsLeader = true;
        }

        UpdateProgressText();
    }

    /// <summary>
    /// 更新进度文本
    /// </summary>
    private void UpdateProgressText()
    {
        var completed = Accounts.Count(a => a.Status == AccountStatus.Completed);
        var total = Accounts.Count(a => a.IsEnabled);
        ProgressText = $"{completed}/{total}";
    }

    #endregion
}
