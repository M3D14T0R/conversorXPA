using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private const int LargeParallelTaskDegreeCap = 4;
    private const int LargeParallelTaskWeightThreshold = 25_000;
    private const int LargeParallelHeadWeightThreshold = 100_000;
    private const int ParallelWorkerStackSizeBytes = 16 * 1024 * 1024;

    private static IReadOnlyList<TaskSemantic> ScheduleParallelSkeletonTasks(
        IReadOnlyList<TaskSemantic> skeletonTasks,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (skeletonTasks.Count <= 1)
            return skeletonTasks;

        var childrenByParent = allTasks
            .Where(t => t.ParentOrdinal.HasValue)
            .GroupBy(t => t.ParentOrdinal!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskSemantic>)g.ToList());
        var weightCache = new Dictionary<int, long>();

        return skeletonTasks
            .Select((task, index) => new
            {
                Task = task,
                Index = index,
                Weight = EstimateTaskTreeGenerationWeight(task, childrenByParent, weightCache)
            })
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Index)
            .Select(x => x.Task)
            .ToList();
    }

    private static void RunParallelSkeletonTaskQueue(
        IReadOnlyList<TaskSemantic> scheduledTasks,
        int requestedDegree,
        Action<TaskSemantic> emitTask)
    {
        if (scheduledTasks.Count == 0)
            return;

        var degree = ResolveParallelSkeletonTaskDegree(scheduledTasks, requestedDegree);
        var queue = new ConcurrentQueue<TaskSemantic>(scheduledTasks);
        var exceptions = new ConcurrentQueue<Exception>();
        var workers = new Thread[degree];

        for (var workerIndex = 0; workerIndex < degree; workerIndex++)
        {
            var name = $"xpa-task-emitter-{workerIndex + 1}";
            workers[workerIndex] = new Thread(() =>
            {
                var workerState = CreateIsolatedGenerationState();
                try
                {
                    while (queue.TryDequeue(out var task))
                        RunWithGenerationState(workerState, () => emitTask(task));
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }, ParallelWorkerStackSizeBytes)
            {
                IsBackground = false,
                Name = name
            };
        }

        foreach (var worker in workers)
            worker.Start();
        foreach (var worker in workers)
            worker.Join();

        if (!exceptions.IsEmpty)
            throw new AggregateException(exceptions);
    }

    private static int ResolveParallelSkeletonTaskDegree(IReadOnlyList<TaskSemantic> scheduledTasks, int requestedDegree)
    {
        if (scheduledTasks.Count == 0)
            return 1;

        var requested = ResolveConfiguredParallelTaskDegree(requestedDegree);
        var degree = Math.Min(Math.Max(1, requested), scheduledTasks.Count);
        if (degree <= 1)
            return degree;

        var weightCache = new Dictionary<int, long>();
        var headWeight = 0L;
        var hasLargeTask = false;
        foreach (var task in scheduledTasks.Take(degree))
        {
            var weight = EstimateTaskTreeGenerationWeight(task, _childTasksByParentOrdinal, weightCache);
            headWeight += weight;
            if (weight >= LargeParallelTaskWeightThreshold)
                hasLargeTask = true;
        }

        if (hasLargeTask || headWeight >= LargeParallelHeadWeightThreshold)
            degree = Math.Min(degree, LargeParallelTaskDegreeCap);

        return Math.Max(1, degree);
    }

    private static int ResolveConfiguredParallelTaskDegree(int requestedDegree)
    {
        var configured = Environment.GetEnvironmentVariable("XPA_CONVERTER_PARALLEL_TASK_DEGREE");
        if (int.TryParse(configured, out var parsed) && parsed > 0)
            return parsed;

        return requestedDegree;
    }

    private static string FormatParallelScheduleHead(IReadOnlyList<TaskSemantic> scheduledTasks)
        => string.Join(", ",
            scheduledTasks
                .Take(Math.Min(Math.Max(1, Environment.ProcessorCount), scheduledTasks.Count))
                .Select(t => ResolveTaskClassName(t, _allTasks)));

    private static long EstimateTaskTreeGenerationWeight(
        TaskSemantic task,
        IReadOnlyDictionary<int, IReadOnlyList<TaskSemantic>> childrenByParent,
        Dictionary<int, long> weightCache)
    {
        if (weightCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var weight = EstimateTaskLocalGenerationWeight(task);
        if (childrenByParent.TryGetValue(task.Ordinal, out var children))
        {
            foreach (var child in children)
                weight += EstimateTaskTreeGenerationWeight(child, childrenByParent, weightCache);
        }

        weightCache[task.Ordinal] = Math.Max(1, weight);
        return weightCache[task.Ordinal];
    }

    private static long EstimateTaskLocalGenerationWeight(TaskSemantic task)
    {
        long weight = 1;
        weight += Math.Max(0, task.TotalVariabls ?? 0);
        weight += Math.Max(0, task.TotalVirtuals ?? 0);
        weight += task.ResourceColumns.Count * 2L;
        weight += task.Selects.Count * 8L;
        weight += task.Links.Count * 16L;
        weight += task.RowLogics.Count * 24L;
        weight += task.SavingRowLogics.Count * 24L;
        weight += task.StartLogics.Count * 16L;
        weight += task.EndLogics.Count * 16L;
        weight += task.Expressions.Count * 8L;
        weight += task.FunctionOverrides.Count * 12L;
        weight += task.Handlers.Count * 12L;
        weight += task.Events.Count * 10L;
        weight += task.FormEntries.Count * 3L;
        weight += task.FormIos.Count * 5L;
        weight += task.Ios.Count * 5L;
        weight += task.GroupLogics.Count * 8L;
        weight += task.Gaps.Count;
        return weight;
    }
}
