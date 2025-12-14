using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaaFramework.Binding;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Models.MultiInstance;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Services;

/// <summary>
/// 多实例任务调度器 - 负责管理并行执行的任务队列和 Worker 池
/// </summary>
public class MultiInstanceScheduler : IDisposable
{
    private readonly ConcurrentQueue<WorkItem> _taskQueue = new();
    private readonly List<TaskWorker> _workers = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// 最大并发 Worker 数量
    /// </summary>
    public int MaxConcurrency { get; }

    /// <summary>
    /// 当前活跃的 Worker 数量
    /// </summary>
    public int ActiveWorkerCount => _workers.Count(w => w.IsRunning);

    /// <summary>
    /// 队列中待处理的任务数量
    /// </summary>
    public int PendingTaskCount => _taskQueue.Count;

    public MultiInstanceScheduler(int maxConcurrency = 2)
    {
        MaxConcurrency = maxConcurrency;
        LoggerHelper.Info($"[Scheduler] 初始化调度器，最大并发数: {maxConcurrency}");
    }

    /// <summary>
    /// 提交任务到队列
    /// </summary>
    public void EnqueueTask(WorkItem workItem)
    {
        _taskQueue.Enqueue(workItem);
        LoggerHelper.Info($"[Scheduler] 任务入队: 账号 [{workItem.Account.Name}:{GetPort(workItem.Account)}], 任务数: {workItem.Tasks.Count}");
    }

    /// <summary>
    /// 开始处理队列中的所有任务 - 真正并行执行
    /// </summary>
    public async Task ProcessAllAsync()
    {
        LoggerHelper.Info($"[Scheduler] 开始处理队列，待处理任务数: {_taskQueue.Count}");

        // 取出所有任务
        var allWorkItems = new List<WorkItem>();
        while (_taskQueue.TryDequeue(out var item))
        {
            allWorkItems.Add(item);
        }

        if (allWorkItems.Count == 0)
        {
            LoggerHelper.Warning("[Scheduler] 没有任务需要处理");
            return;
        }

        LoggerHelper.Info($"[Scheduler] 准备并行执行 {allWorkItems.Count} 个任务，最大并发: {MaxConcurrency}");

        // 第一步：串行创建所有 Worker 和 Tasker（避免资源冲突）
        var workers = new List<(TaskWorker worker, WorkItem workItem)>();
        var workerId = 0;

        foreach (var workItem in allWorkItems)
        {
            if (_cts.Token.IsCancellationRequested) break;

            var currentWorkerId = ++workerId;
            LoggerHelper.Info($"[Scheduler] 创建 Worker #{currentWorkerId} 的 Tasker: 账号 [{workItem.Account.Name}:{GetPort(workItem.Account)}]");

            var worker = new TaskWorker(currentWorkerId, workItem);
            var success = await worker.InitializeTaskerAsync(_cts.Token);

            if (success)
            {
                workers.Add((worker, workItem));
                lock (_workers)
                {
                    _workers.Add(worker);
                }
            }
            else
            {
                LoggerHelper.Error($"[Scheduler] Worker #{currentWorkerId} Tasker 创建失败");
                workItem.Account.Status = AccountStatus.Failed;
            }
        }

        LoggerHelper.Info($"[Scheduler] 成功创建 {workers.Count} 个 Worker，开始并行执行任务");

        // 第二步：并行执行所有 Worker 的任务
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrency,
            CancellationToken = _cts.Token
        };

        await Parallel.ForEachAsync(workers, options, async (item, ct) =>
        {
            var (worker, workItem) = item;

            try
            {
                LoggerHelper.Info($"[Scheduler] Worker #{worker.WorkerId} 开始执行任务: 账号 [{workItem.Account.Name}:{GetPort(workItem.Account)}]");

                await worker.ExecuteTasksAsync(ct);

                LoggerHelper.Info($"[Scheduler] Worker #{worker.WorkerId} 执行完成: 账号 [{workItem.Account.Name}:{GetPort(workItem.Account)}]");
            }
            catch (OperationCanceledException)
            {
                LoggerHelper.Warning($"[Scheduler] Worker #{worker.WorkerId} 被取消");
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"[Scheduler] Worker #{worker.WorkerId} 执行失败: {ex.Message}");
            }
        });

        LoggerHelper.Info($"[Scheduler] 所有任务处理完成");
    }

    /// <summary>
    /// 停止调度器
    /// </summary>
    public void Stop()
    {
        LoggerHelper.Info($"[Scheduler] 停止调度器");
        _cts.Cancel();
    }

    private string GetPort(MultiAccount account)
    {
        if (string.IsNullOrEmpty(account.AdbSerial))
            return "unknown";
        var parts = account.AdbSerial.Split(':');
        return parts.Length > 1 ? parts[1] : account.AdbSerial;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _cts.Dispose();

        foreach (var worker in _workers)
        {
            worker.Dispose();
        }
        _workers.Clear();
    }
}

/// <summary>
/// 工作项 - 包含账号和要执行的任务列表
/// </summary>
public class WorkItem
{
    public MultiAccount Account { get; set; } = null!;
    public List<MultiTaskItem> Tasks { get; set; } = new();
    public string TaskType { get; set; } = "单人任务";
}

/// <summary>
/// 任务 Worker - 负责执行单个账号的任务
/// </summary>
public class TaskWorker : IDisposable
{
    private readonly int _workerId;
    private readonly WorkItem _workItem;
    private MaaTasker? _tasker;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public int WorkerId => _workerId;

    public TaskWorker(int workerId, WorkItem workItem)
    {
        _workerId = workerId;
        _workItem = workItem;
    }

    /// <summary>
    /// 初始化 Tasker（串行调用，避免资源冲突）
    /// </summary>
    public async Task<bool> InitializeTaskerAsync(CancellationToken cancellationToken)
    {
        var displayName = GetDisplayName();

        try
        {
            _workItem.Account.Status = AccountStatus.Running;

            _tasker = await CreateTaskerAsync(displayName, cancellationToken);
            if (_tasker == null)
            {
                LoggerHelper.Error($"[Worker #{_workerId}] 账号 [{displayName}] Tasker 创建失败");
                _workItem.Account.Status = AccountStatus.Failed;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] 账号 [{displayName}] 初始化异常: {ex.Message}");
            _workItem.Account.Status = AccountStatus.Failed;
            return false;
        }
    }

    /// <summary>
    /// 执行所有任务（可以并行调用）
    /// </summary>
    public async Task ExecuteTasksAsync(CancellationToken cancellationToken)
    {
        if (_tasker == null)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] Tasker 未初始化");
            return;
        }

        IsRunning = true;
        var displayName = GetDisplayName();

        try
        {
            foreach (var task in _workItem.Tasks)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _workItem.Account.Status = AccountStatus.Cancelled;
                    break;
                }

                await ExecuteSingleTaskAsync(displayName, task, cancellationToken);

                // 如果任务失败，继续执行下一个任务
                if (_workItem.Account.Status == AccountStatus.Failed)
                {
                    LoggerHelper.Warning($"[Worker #{_workerId}] 账号 [{displayName}] 任务失败，继续执行下一个任务");
                    _workItem.Account.Status = AccountStatus.Running;
                }
            }

            // 如果没有被取消，标记为完成
            if (_workItem.Account.Status != AccountStatus.Cancelled)
            {
                _workItem.Account.Status = AccountStatus.Completed;
            }

            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 所有任务完成");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] 账号 [{displayName}] 执行异常: {ex.Message}");
            _workItem.Account.Status = AccountStatus.Failed;
        }
        finally
        {
            IsRunning = false;
            Dispose();
        }
    }

    /// <summary>
    /// 执行任务（旧的入口方法，保持向后兼容）
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!await InitializeTaskerAsync(cancellationToken))
        {
            return;
        }

        await ExecuteTasksAsync(cancellationToken);
    }

    private async Task<MaaTasker?> CreateTaskerAsync(string displayName, CancellationToken cancellationToken)
    {
        try
        {
            var account = _workItem.Account;

            // 创建独立的 Toolkit 实例用于查找设备
            var toolkit = new MaaToolkit(true);

            // 查找设备
            var devices = toolkit.AdbDevice.Find();
            var device = devices.FirstOrDefault(d =>
                string.Equals(d.AdbSerial, account.AdbSerial, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                LoggerHelper.Error($"[Worker #{_workerId}] 未找到设备: {account.AdbSerial}");
                return null;
            }

            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 找到设备: {device.AdbSerial}");

            // 获取资源路径
            var currentResource = Instances.TaskQueueViewModel.CurrentResources
                .FirstOrDefault(c => c.Name == Instances.TaskQueueViewModel.CurrentResource);
            var resources = currentResource?.ResolvedPath ?? currentResource?.Path ?? new List<string>();
            resources = resources.Select(System.IO.Path.GetFullPath).ToList();

            // 在后台线程创建资源
            var maaResource = await Task.Run(() => new MaaResource(resources), cancellationToken);

            // 应用 GPU 选项（与主程序一致）
            Instances.PerformanceUserControlModel.ChangeGpuOption(maaResource, Instances.PerformanceUserControlModel.GpuOption);

            // 处理 InputType 和 ScreenCapType
            var adbInputType = Instances.ConnectSettingsUserControlModel.AdbControlInputType;
            var adbScreenCapType = Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType;

            if (adbInputType == AdbInputMethods.None)
            {
                adbInputType = device.InputMethods != AdbInputMethods.None
                    ? device.InputMethods
                    : AdbInputMethods.Default;
            }

            if (adbScreenCapType == AdbScreencapMethods.None)
            {
                adbScreenCapType = device.ScreencapMethods != AdbScreencapMethods.None
                    ? device.ScreencapMethods
                    : AdbScreencapMethods.Default;
            }

            // 在后台线程创建控制器
            var maaController = await Task.Run(() => new MaaAdbController(
                device.AdbPath,
                device.AdbSerial,
                adbScreenCapType,
                adbInputType,
                !string.IsNullOrWhiteSpace(device.Config) ? device.Config : "{}",
                System.IO.Path.Combine(AppContext.BaseDirectory, "MaaAgentBinary")
            ), cancellationToken);

            // 创建独立的 Toolkit 和 Global 实例（避免共享资源冲突）
            var workerToolkit = new MaaToolkit(true);
            var workerGlobal = new MaaGlobal();

            // 创建 Tasker - 使用独立的实例
            var tasker = new MaaTasker
            {
                Controller = maaController,
                Resource = maaResource,
                Toolkit = workerToolkit,
                Global = workerGlobal,
                DisposeOptions = DisposeOptions.All,
            };

            // 等待初始化
            var initStartTime = DateTime.Now;
            while (!tasker.IsInitialized && (DateTime.Now - initStartTime).TotalSeconds < 30)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tasker.Dispose();
                    return null;
                }
                await Task.Delay(100, cancellationToken);
            }

            if (!tasker.IsInitialized)
            {
                LoggerHelper.Error($"[Worker #{_workerId}] Tasker 初始化超时");
                tasker.Dispose();
                return null;
            }

            // 测试设备连接
            var screencapJob = tasker.Controller.Screencap();
            var screencapStartTime = DateTime.Now;
            while ((DateTime.Now - screencapStartTime).TotalSeconds < 30)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tasker.Dispose();
                    return null;
                }

                var status = screencapJob.Status;
                if (status == MaaJobStatus.Succeeded)
                {
                    LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] Tasker 创建成功");
                    return tasker;
                }
                if (status == MaaJobStatus.Failed || status == MaaJobStatus.Invalid)
                {
                    LoggerHelper.Error($"[Worker #{_workerId}] 设备连接测试失败: {status}");
                    tasker.Dispose();
                    return null;
                }

                await Task.Delay(100, cancellationToken);
            }

            LoggerHelper.Error($"[Worker #{_workerId}] 设备连接测试超时");
            tasker.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] 创建 Tasker 失败: {ex.Message}");
            return null;
        }
    }

    private async Task ExecuteSingleTaskAsync(string displayName, MultiTaskItem task, CancellationToken cancellationToken)
    {
        if (_tasker == null || string.IsNullOrEmpty(task.Entry))
        {
            LoggerHelper.Warning($"[Worker #{_workerId}] 跳过任务: Tasker或Entry为空");
            return;
        }

        try
        {
            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 开始执行任务: {task.DisplayName} (Entry: {task.Entry})");

            // 构建任务参数
            var taskParams = BuildTaskParams(task);

            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 调用 AppendTask...");
            var taskJob = _tasker.AppendTask(task.Entry, taskParams);
            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] AppendTask 返回 JobId={taskJob.Id}");

            var startTime = DateTime.Now;

            // 在后台线程中阻塞等待任务完成（这样不会阻塞其他并行任务）
            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 开始 Wait()...");
            var jobStatus = await Task.Run(() =>
            {
                try
                {
                    var status = taskJob.Wait();
                    return status;
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"[Worker #{_workerId}] 任务等待异常: {ex.Message}");
                    return MaaJobStatus.Failed;
                }
            }, cancellationToken);

            var elapsed = (DateTime.Now - startTime).TotalSeconds;
            LoggerHelper.Info($"[Worker #{_workerId}] 账号 [{displayName}] 任务 {task.DisplayName} 完成: {jobStatus} (耗时: {elapsed:F1}秒)");

            // 更新账号状态
            if (jobStatus == MaaJobStatus.Succeeded)
            {
                _workItem.Account.Status = AccountStatus.Running;
            }
            else
            {
                _workItem.Account.Status = AccountStatus.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning($"[Worker #{_workerId}] 任务 {task.DisplayName} 被取消");
            _workItem.Account.Status = AccountStatus.Cancelled;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] 执行任务 {task.DisplayName} 失败: {ex.Message}");
            _workItem.Account.Status = AccountStatus.Failed;
        }
    }

    /// <summary>
    /// 构建任务参数 - 使用与主程序相同的逻辑
    /// </summary>
    private string BuildTaskParams(MultiTaskItem task)
    {
        try
        {
            var interfaceTask = task.InterfaceTask;
            if (interfaceTask == null)
            {
                return "{}";
            }

            // 1. 获取 PipelineOverride 并转为 MaaToken
            var pipelineOverride = interfaceTask.PipelineOverride ?? new Dictionary<string, JToken>();

            var json = JsonConvert.SerializeObject(pipelineOverride, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            });

            var taskModels = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(json);

            // 2. 转换为 MaaToken
            var maaToken = taskModels.ToMaaToken();

            // 3. 处理 Option 配置（与主页面 MaaProcessor.UpdateTaskDictionary 相同）
            if (task.Option != null)
            {
                ProcessOptions(ref maaToken, task.Option);
            }

            // 4. 处理 Advanced 配置
            if (task.Advanced != null)
            {
                foreach (var selectAdvanced in task.Advanced)
                {
                    if (!string.IsNullOrWhiteSpace(selectAdvanced.PipelineOverride) && selectAdvanced.PipelineOverride != "{}")
                    {
                        var param = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(selectAdvanced.PipelineOverride);
                        maaToken.Merge(param);
                    }
                }
            }

            // 5. 序列化为字符串
            return maaToken.ToString();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] 构建任务参数失败: {ex.Message}");
            return "{}";
        }
    }

    /// <summary>
    /// 处理 Option 配置（复制自 MaaProcessor.ProcessOptions）
    /// </summary>
    private void ProcessOptions(
        ref MaaToken taskModels,
        List<MaaInterface.MaaInterfaceSelectOption> allOptions,
        List<string>? optionNamesToProcess = null,
        HashSet<string>? processedOptions = null)
    {
        processedOptions ??= new HashSet<string>();

        // 确定要处理的 options
        IEnumerable<MaaInterface.MaaInterfaceSelectOption> optionsToProcess;

        if (optionNamesToProcess == null)
        {
            optionsToProcess = allOptions;
        }
        else
        {
            optionsToProcess = allOptions.Where(o => optionNamesToProcess.Contains(o.Name ?? string.Empty));
        }

        foreach (var selectOption in optionsToProcess)
        {
            var optionName = selectOption.Name ?? string.Empty;

            if (processedOptions.Contains(optionName))
                continue;

            processedOptions.Add(optionName);

            // 获取 Interface.Option 中的配置
            if (MaaProcessor.Interface?.Option?.TryGetValue(optionName, out var interfaceOption) != true)
                continue;

            // 处理 input 类型
            if (interfaceOption.IsInput)
            {
                string? pipelineOverride = selectOption.PipelineOverride;

                if ((string.IsNullOrWhiteSpace(pipelineOverride) || pipelineOverride == "{}")
                    && selectOption.Data != null
                    && interfaceOption.PipelineOverride != null)
                {
                    pipelineOverride = interfaceOption.GenerateProcessedPipeline(
                        selectOption.Data.Where(kv => kv.Value != null)
                            .ToDictionary(kv => kv.Key, kv => kv.Value!));
                }

                if (!string.IsNullOrWhiteSpace(pipelineOverride) && pipelineOverride != "{}")
                {
                    var param = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(pipelineOverride);
                    taskModels.Merge(param);
                }
            }
            // 处理 select/switch 类型
            else if (selectOption.Index is int index
                     && interfaceOption.Cases is { } cases
                     && index >= 0
                     && index < cases.Count)
            {
                var selectedCase = cases[index];

                // 合并当前 case 的 pipeline_override
                if (selectedCase.PipelineOverride != null)
                {
                    taskModels.Merge(selectedCase.PipelineOverride);
                }

                // 递归处理子配置项
                if (selectedCase.Option != null && selectedCase.Option.Count > 0)
                {
                    var unprocessedSubOptionNames = selectedCase.Option
                        .Where(name => !processedOptions.Contains(name))
                        .ToList();

                    if (unprocessedSubOptionNames.Count > 0 && selectOption.SubOptions != null)
                    {
                        var subOptionsToProcess = selectOption.SubOptions
                            .Where(s => unprocessedSubOptionNames.Contains(s.Name ?? string.Empty))
                            .ToList();

                        ProcessOptions(ref taskModels, subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
                    }
                    else if (unprocessedSubOptionNames.Count > 0)
                    {
                        ProcessOptions(ref taskModels, allOptions, unprocessedSubOptionNames, processedOptions);
                    }
                }
            }
        }
    }

    private string GetDisplayName()
    {
        if (string.IsNullOrEmpty(_workItem.Account.AdbSerial))
            return _workItem.Account.Name;

        var parts = _workItem.Account.AdbSerial.Split(':');
        var port = parts.Length > 1 ? parts[1] : _workItem.Account.AdbSerial;
        return $"{_workItem.Account.Name}:{port}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _tasker?.Dispose();
            _tasker = null;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[Worker #{_workerId}] Dispose Tasker 失败: {ex.Message}");
        }
    }
}
