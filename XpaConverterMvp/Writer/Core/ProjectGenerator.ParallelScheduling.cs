using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private const int LargeParallelTaskDegreeCap = 2;
    private const int LargeParallelTaskWeightThreshold = 25_000;
    private const int LargeParallelHeadWeightThreshold = 100_000;
    private const int ParallelWorkerStackSizeBytes = 16 * 1024 * 1024;
    private const double DefaultParallelMemoryPauseGb = 16;
    private const double DefaultParallelHardMemoryPauseGb = 18;
    private const double DefaultParallelDynamicWorkerSoftPrivateGb = 10;
    private const double DefaultParallelDynamicWorkerMediumPrivateGb = 12;
    private const double DefaultParallelDynamicWorkerHardPrivateGb = 14;
    private const double DefaultParallelWorkerCacheResetPrivateGb = 16;

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

        var workerDegree = ResolveParallelSkeletonWorkerDegree(scheduledTasks, requestedDegree);
        var heavyDegreeCap = ResolveParallelSkeletonHeavyDegreeCap(scheduledTasks, requestedDegree);
        var weightedTasks = BuildWeightedSkeletonTasks(scheduledTasks);
        var remainingTasks = new List<(TaskSemantic Task, long Weight)>(weightedTasks);
        var sync = new object();
        var activeTasks = 0;
        var activeHeavyTasks = 0;
        var exceptions = new ConcurrentQueue<Exception>();
        var workers = new Thread[workerDegree];

        bool TryTakeNext(int workerIndex, out (TaskSemantic Task, long Weight) item)
        {
            lock (sync)
            {
                while (true)
                {
                    if (remainingTasks.Count == 0)
                    {
                        item = default;
                        return false;
                    }

                    var activeWorkerLimit = ResolveDynamicParallelActiveWorkerLimit(
                        workerDegree,
                        remainingTasks.Count,
                        weightedTasks.Count);
                    if (workerIndex >= activeWorkerLimit)
                    {
                        Monitor.Wait(sync, TimeSpan.FromSeconds(2));
                        continue;
                    }

                    // Never let a high working set start another concurrent
                    // program. If every worker is idle, allow exactly one to
                    // continue so a reserved (but not live) GC heap cannot
                    // deadlock the queue above the threshold.
                    if (activeTasks > 0 && ShouldPauseParallelSchedulingForMemory())
                    {
                        Monitor.Wait(sync, TimeSpan.FromSeconds(2));
                        continue;
                    }

                    var selectedIndex = -1;
                    for (var i = 0; i < remainingTasks.Count; i++)
                    {
                        var candidate = remainingTasks[i];
                        if (!IsLargeParallelSkeletonTask(candidate.Weight) || activeHeavyTasks < heavyDegreeCap)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }

                    if (selectedIndex >= 0)
                    {
                        item = remainingTasks[selectedIndex];
                        remainingTasks.RemoveAt(selectedIndex);
                        activeTasks++;
                        if (IsLargeParallelSkeletonTask(item.Weight))
                            activeHeavyTasks++;
                        return true;
                    }

                    Monitor.Wait(sync);
                }
            }
        }

        void Complete((TaskSemantic Task, long Weight) item)
        {
            var isLarge = IsLargeParallelSkeletonTask(item.Weight);

            lock (sync)
            {
                activeTasks = Math.Max(0, activeTasks - 1);
                if (isLarge)
                    activeHeavyTasks = Math.Max(0, activeHeavyTasks - 1);
                Monitor.PulseAll(sync);
            }

            lock (sync)
            {
                Monitor.PulseAll(sync);
            }
        }

        for (var workerIndex = 0; workerIndex < workerDegree; workerIndex++)
        {
            var workerOrdinal = workerIndex;
            var name = $"xpa-task-emitter-{workerIndex + 1}";
            workers[workerIndex] = new Thread(() =>
            {
                var taskState = CreateIsolatedGenerationState();
                try
                {
                    while (TryTakeNext(workerOrdinal, out var item))
                    {
                        // Expression and resolution caches are valid only for
                        // the program currently being emitted. Reusing them for
                        // every program made each worker retain an ever-growing
                        // graph for the entire conversion (catastrophic on
                        // projects such as CGGeral). Keep the immutable global
                        // indexes shared and give each output unit fresh caches.
                        try
                        {
                            RunWithGenerationState(taskState, () => emitTask(item.Task));
                        }
                        finally
                        {
                            // Release the just-completed program's expression
                            // graph before a threshold-triggered collection.
                            taskState = CreateNextProgramGenerationState(taskState);
                            Complete(item);
                        }
                    }
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
        => ResolveParallelSkeletonHeavyDegreeCap(scheduledTasks, requestedDegree);

    private static int ResolveParallelSkeletonWorkerDegree(IReadOnlyList<TaskSemantic> scheduledTasks, int requestedDegree)
    {
        if (scheduledTasks.Count == 0)
            return 1;

        var requested = ResolveConfiguredParallelTaskDegree(requestedDegree);
        var degree = Math.Min(Math.Max(1, requested), scheduledTasks.Count);
        return Math.Max(1, degree);
    }

    private static int ResolveParallelSkeletonHeavyDegreeCap(IReadOnlyList<TaskSemantic> scheduledTasks, int requestedDegree)
    {
        if (scheduledTasks.Count == 0)
            return 1;

        var degree = ResolveParallelSkeletonWorkerDegree(scheduledTasks, requestedDegree);
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

    private static IReadOnlyList<(TaskSemantic Task, long Weight)> BuildWeightedSkeletonTasks(IReadOnlyList<TaskSemantic> scheduledTasks)
    {
        var weightCache = new Dictionary<int, long>();
        return scheduledTasks
            .Select(task => (Task: task, Weight: EstimateTaskTreeGenerationWeight(task, _childTasksByParentOrdinal, weightCache)))
            .ToList();
    }

    private static bool IsLargeParallelSkeletonTask(long weight)
        => weight >= LargeParallelTaskWeightThreshold;

    private static int ResolveDynamicParallelActiveWorkerLimit(int maxWorkers, int remainingTasks, int totalTasks)
    {
        if (maxWorkers <= 1 || !ResolveConfiguredBool("XPA_CONVERTER_PARALLEL_DYNAMIC_WORKERS", defaultValue: true))
            return maxWorkers;

        var minWorkers = Math.Clamp(
            ResolveConfiguredInt("XPA_CONVERTER_PARALLEL_MIN_WORKERS", Math.Min(2, maxWorkers)),
            1,
            maxWorkers);
        var privateGb = GetCurrentPrivateMemoryGb();
        var softGb = ResolveConfiguredMemoryLimitGb("XPA_CONVERTER_PARALLEL_DYNAMIC_SOFT_GB", DefaultParallelDynamicWorkerSoftPrivateGb);
        var mediumGb = ResolveConfiguredMemoryLimitGb("XPA_CONVERTER_PARALLEL_DYNAMIC_MEDIUM_GB", DefaultParallelDynamicWorkerMediumPrivateGb);
        var hardGb = ResolveConfiguredMemoryLimitGb("XPA_CONVERTER_PARALLEL_DYNAMIC_HARD_GB", DefaultParallelDynamicWorkerHardPrivateGb);

        var limit = minWorkers;
        if (privateGb < softGb)
            limit = maxWorkers;
        else if (privateGb < mediumGb)
            limit = Math.Max(minWorkers, maxWorkers - 1);
        else if (privateGb < hardGb)
            limit = Math.Max(minWorkers, maxWorkers - 2);

        var initialUntil = Math.Clamp(
            ResolveConfiguredDouble("XPA_CONVERTER_PARALLEL_RAMP_INITIAL_UNTIL", 0),
            0,
            1);
        if (initialUntil > 0 && totalTasks > 0)
        {
            var completedFraction = (totalTasks - remainingTasks) / (double)totalTasks;
            if (completedFraction < initialUntil)
            {
                var initialWorkers = Math.Clamp(
                    ResolveConfiguredInt("XPA_CONVERTER_PARALLEL_RAMP_INITIAL_WORKERS", minWorkers),
                    minWorkers,
                    maxWorkers);
                limit = Math.Min(limit, initialWorkers);
            }
        }

        var defaultInitialTaskCount = totalTasks >= 1_000 ? 32 : 0;
        var initialTaskCount = Math.Max(0,
            ResolveConfiguredInt("XPA_CONVERTER_PARALLEL_RAMP_INITIAL_TASKS", defaultInitialTaskCount));
        if (initialTaskCount > 0 && totalTasks - remainingTasks < initialTaskCount)
        {
            var initialWorkers = Math.Clamp(
                ResolveConfiguredInt("XPA_CONVERTER_PARALLEL_RAMP_INITIAL_WORKERS", minWorkers),
                minWorkers,
                maxWorkers);
            limit = Math.Min(limit, initialWorkers);
        }

        return limit;
    }

    private static bool ShouldResetParallelWorkerState(int completedSinceStateReset)
    {
        var everyTasks = ResolveConfiguredInt("XPA_CONVERTER_CACHE_RESET_EVERY_TASKS", 0);
        if (everyTasks > 0 && completedSinceStateReset >= everyTasks)
            return true;

        var thresholdGb = ResolveConfiguredMemoryLimitGb("XPA_CONVERTER_CACHE_RESET_MEMORY_GB", DefaultParallelWorkerCacheResetPrivateGb);
        return thresholdGb > 0 && GetCurrentPrivateMemoryGb() >= thresholdGb;
    }

    private static bool ShouldPauseParallelSchedulingForMemory()
    {
        var thresholdGb = ResolveParallelMemoryPauseGb();
        if (thresholdGb <= 0)
            return false;

        var hardPrivateMemoryGb = ResolveConfiguredMemoryLimitGb(
            "XPA_CONVERTER_PARALLEL_HARD_MEMORY_PAUSE_GB",
            DefaultParallelHardMemoryPauseGb);
        var privateMemoryGb = GetCurrentPrivateMemoryGb();
        var managedHeapGb = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d / 1024d;
        if (managedHeapGb < thresholdGb &&
            (hardPrivateMemoryGb <= 0 || privateMemoryGb < hardPrivateMemoryGb))
        {
            // The GC can retain committed pages after the program graph was
            // released. They remain in the process working set but are
            // immediately reusable, so they must not permanently serialize
            // the rest of the conversion.
            return false;
        }

        return managedHeapGb >= thresholdGb ||
               (hardPrivateMemoryGb > 0 && privateMemoryGb >= hardPrivateMemoryGb);
    }

    private static double ResolveParallelMemoryPauseGb()
    {
        return ResolveConfiguredMemoryLimitGb("XPA_CONVERTER_PARALLEL_MEMORY_PAUSE_GB", DefaultParallelMemoryPauseGb);
    }

    private static string FormatParallelDegreePlan(IReadOnlyList<TaskSemantic> scheduledTasks, int requestedDegree)
    {
        var workerDegree = ResolveParallelSkeletonWorkerDegree(scheduledTasks, requestedDegree);
        var heavyDegreeCap = ResolveParallelSkeletonHeavyDegreeCap(scheduledTasks, requestedDegree);
        return $"workers={workerDegree} heavyCap={heavyDegreeCap} requested={requestedDegree} scheduled=largest-first";
    }

    private static int ResolveConfiguredParallelTaskDegree(int requestedDegree)
    {
        var configured = Environment.GetEnvironmentVariable("XPA_CONVERTER_PARALLEL_TASK_DEGREE");
        if (int.TryParse(configured, out var parsed) && parsed > 0)
            return parsed;

        return requestedDegree;
    }

    private static int ResolveConfiguredInt(string name, int defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        return int.TryParse(configured, out var parsed) ? parsed : defaultValue;
    }

    private static double ResolveConfiguredDouble(string name, double defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            return parsed;

        return defaultValue;
    }

    private static double ResolveConfiguredMemoryLimitGb(string name, double defaultGb)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(configured))
            return defaultGb;

        configured = configured.Trim();
        if (configured.EndsWith("%", StringComparison.Ordinal))
        {
            var percentText = configured[..^1].Trim();
            if (double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) &&
                percent >= 0)
            {
                return GetTotalPhysicalMemoryGb() * percent / 100d;
            }
        }

        if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            if (parsed > 0 && parsed <= 1)
                return GetTotalPhysicalMemoryGb() * parsed;

            return parsed;
        }

        return defaultGb;
    }

    private static bool ResolveConfiguredBool(string name, bool defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(configured))
            return defaultValue;

        return configured.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               configured.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               configured.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               configured.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static double GetCurrentPrivateMemoryGb()
        => Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d / 1024d;

    private static double GetTotalPhysicalMemoryGb()
        => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d;

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
