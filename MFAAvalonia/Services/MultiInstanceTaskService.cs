using System.Collections.ObjectModel;
using System.Linq;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Models.MultiInstance;

namespace MFAAvalonia.Services;

/// <summary>
/// 多实例任务服务（独立于主任务系统）
/// 负责加载和管理多实例页面的任务列表
/// </summary>
public static class MultiInstanceTaskService
{
    /// <summary>
    /// 从 MaaProcessor.Interface 加载所有任务
    /// </summary>
    /// <param name="resourceName">当前选中的资源包名称</param>
    /// <returns>任务列表</returns>
    public static ObservableCollection<MultiTaskItem> LoadTasks(string? resourceName = null)
    {
        var tasks = new ObservableCollection<MultiTaskItem>();

        try
        {
            // 从 MaaProcessor.Interface 获取任务配置
            var interfaceTasks = MaaProcessor.Interface?.Task;

            if (interfaceTasks == null || interfaceTasks.Count == 0)
            {
                LoggerHelper.Warn("[MultiInstance] 未找到任务配置");
                return tasks;
            }

            LoggerHelper.Info($"[MultiInstance] 开始加载任务，共 {interfaceTasks.Count} 个");

            foreach (var interfaceTask in interfaceTasks)
            {
                // 跳过没有 Entry 的任务（无法执行）
                if (string.IsNullOrWhiteSpace(interfaceTask.Entry))
                {
                    LoggerHelper.Debug($"[MultiInstance] 跳过任务 {interfaceTask.Name}：没有 Entry");
                    continue;
                }

                // 检查任务是否支持当前资源包
                if (!IsTaskSupportedByResource(interfaceTask, resourceName))
                {
                    LoggerHelper.Debug($"[MultiInstance] 跳过任务 {interfaceTask.Name}：不支持资源包 {resourceName}");
                    continue;
                }

                // 初始化图标（如果还没初始化）
                if (string.IsNullOrWhiteSpace(interfaceTask.ResolvedIcon) && !string.IsNullOrWhiteSpace(interfaceTask.Icon))
                {
                    interfaceTask.InitializeIcon();
                }

                // 转换为 MultiTaskItem
                var taskItem = MultiTaskItem.FromInterfaceTask(interfaceTask);
                tasks.Add(taskItem);

                LoggerHelper.Debug($"[MultiInstance] 加载任务: {taskItem.DisplayName} (Entry: {taskItem.Entry})");
            }

            LoggerHelper.Info($"[MultiInstance] 任务加载完成，共 {tasks.Count} 个可用任务");

            // 输出所有任务的名称和显示名称（便于调试匹配问题）
            if (tasks.Count > 0)
            {
                LoggerHelper.Info("[MultiInstance] ========== 所有任务列表 ==========");
                foreach (var task in tasks)
                {
                    LoggerHelper.Info($"  - DisplayName: '{task.DisplayName}' | Name: '{task.Name}'");
                }
                LoggerHelper.Info("[MultiInstance] =====================================");
            }
        }
        catch (System.Exception ex)
        {
            LoggerHelper.Error($"[MultiInstance] 加载任务失败: {ex.Message}");
        }

        return tasks;
    }

    /// <summary>
    /// 检查任务是否支持指定的资源包
    /// </summary>
    private static bool IsTaskSupportedByResource(MaaInterface.MaaInterfaceTask task, string? resourceName)
    {
        // 如果任务没有指定 resource，则支持所有资源包
        if (task.Resource == null || task.Resource.Count == 0)
            return true;

        // 如果资源名称为空，则显示所有任务
        if (string.IsNullOrWhiteSpace(resourceName))
            return true;

        // 检查任务是否支持当前资源包
        return task.Resource.Any(r =>
            r.Equals(resourceName, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 将任务列表分类为单人任务和组队任务
    /// （目前暂时全部归为单人任务，后续用户会标记哪些是组队任务）
    /// </summary>
    public static (ObservableCollection<MultiTaskItem> SoloTasks, ObservableCollection<MultiTaskItem> TeamTasks)
        ClassifyTasks(ObservableCollection<MultiTaskItem> allTasks)
    {
        var soloTasks = new ObservableCollection<MultiTaskItem>();
        var teamTasks = new ObservableCollection<MultiTaskItem>();

        foreach (var task in allTasks)
        {
            if (task.TaskType == MultiTaskType.Team)
            {
                teamTasks.Add(task);
            }
            else
            {
                soloTasks.Add(task);
            }
        }

        return (soloTasks, teamTasks);
    }

    /// <summary>
    /// 标记指定任务为组队任务
    /// </summary>
    /// <param name="tasks">任务列表</param>
    /// <param name="taskNames">需要标记为组队任务的任务名称列表</param>
    public static void MarkAsTeamTasks(ObservableCollection<MultiTaskItem> tasks, params string[] taskNames)
    {
        foreach (var taskName in taskNames)
        {
            // 同时匹配 Name 和 DisplayName
            var task = tasks.FirstOrDefault(t =>
                t.Name.Equals(taskName, System.StringComparison.OrdinalIgnoreCase) ||
                t.DisplayName.Equals(taskName, System.StringComparison.OrdinalIgnoreCase));

            if (task != null)
            {
                task.TaskType = MultiTaskType.Team;
                LoggerHelper.Info($"[MultiInstance] 任务 '{task.DisplayName}' (Name: {task.Name}) 标记为组队任务");
            }
            else
            {
                LoggerHelper.Warn($"[MultiInstance] 未找到任务: '{taskName}'");
            }
        }
    }

    /// <summary>
    /// 标记以指定前缀开头的任务为组队任务
    /// </summary>
    /// <param name="tasks">任务列表</param>
    /// <param name="prefixes">需要标记为组队任务的任务名称前缀列表</param>
    public static void MarkAsTeamTasksByPrefix(ObservableCollection<MultiTaskItem> tasks, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            // 同时匹配 Name 和 DisplayName 的前缀
            var matchedTasks = tasks.Where(t =>
                t.Name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) ||
                t.DisplayName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)).ToList();

            if (matchedTasks.Count == 0)
            {
                LoggerHelper.Warn($"[MultiInstance] 未找到前缀为 '{prefix}' 的任务");
            }

            foreach (var task in matchedTasks)
            {
                task.TaskType = MultiTaskType.Team;
                LoggerHelper.Info($"[MultiInstance] 任务 '{task.DisplayName}' (Name: {task.Name}, 前缀: {prefix}) 标记为组队任务");
            }
        }
    }
}
