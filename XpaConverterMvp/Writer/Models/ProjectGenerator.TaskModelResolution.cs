using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveTaskModelInitializer(TaskSemantic task, int dbObj, int? primaryObj, bool isLinkMember = false)
    {
        var initParts = new List<string>();
        var effectivePrimaryObj = primaryObj ?? task.PrimaryDbObj ?? task.InformationDbObj;
        var isWriteRelationTarget = task.Links.Any(x => x.DbObj == dbObj &&
            (string.Equals(x.Mode, "W", StringComparison.OrdinalIgnoreCase) ||
             (string.Equals(x.Mode, "A", StringComparison.OrdinalIgnoreCase) &&
              !task.DataView.PrimaryDataObject.HasValue &&
              string.Equals(task.ResourceDbs.FirstOrDefault(r => r.DataObject == dbObj)?.Access, "W", StringComparison.OrdinalIgnoreCase))));
        var taskWantsImplicitCachedWriteModel =
            !isLinkMember &&
            !isWriteRelationTarget &&
            task.ResourceDbs.Count == 1 &&
            string.IsNullOrWhiteSpace(task.Execution.Activity) &&
            string.IsNullOrWhiteSpace(task.Execution.RowLocking) &&
            string.IsNullOrWhiteSpace(task.Execution.TransactionScope);
        var singleWriteDb = task.ResourceDbs.Count == 1 &&
                            string.Equals(task.ResourceDbs[0].Access, "W", StringComparison.OrdinalIgnoreCase);
        var singleWriteDbPreferAllowRowLockingOnly =
            singleWriteDb &&
            string.Equals(task.CacheStrategy, "T", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionMode, "P", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(task.TransactionBegin, "N", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(task.TransactionBegin, "L", StringComparison.OrdinalIgnoreCase));
        var singleWriteDbPreferCachedWritablePrimary =
            singleWriteDb &&
            effectivePrimaryObj.HasValue &&
            task.ResourceDbs[0].DataObject == effectivePrimaryObj.Value &&
            string.Equals(task.TaskType, "O", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.CacheStrategy, "D", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionBegin, "P", StringComparison.OrdinalIgnoreCase);
        if (!isLinkMember &&
            singleWriteDb &&
            !isWriteRelationTarget &&
            !(taskWantsImplicitCachedWriteModel && task.ResourceDbs[0].Cache == true))
        {
            if (singleWriteDbPreferAllowRowLockingOnly)
                return " { AllowRowLocking = true }";
            if (singleWriteDbPreferCachedWritablePrimary)
                return " { AllowRowLocking = true, Cached = true }";
            return task.ResourceDbs[0].Cache == true
                ? " { AllowRowLocking = true, Cached = true }"
                : " { AllowRowLocking = true }";
        }
        var db = task.ResourceDbs.FirstOrDefault(x => x.DataObject == dbObj)
                 ?? (task.ResourceDbs.Count == 1 ? task.ResourceDbs[0] : null);
        if (db is null && !isLinkMember)
        {
            var preferredDbObj = primaryObj ?? task.PrimaryDbObj ?? task.InformationDbObj;
            if (preferredDbObj.HasValue)
            {
                var matchingPrimary = task.ResourceDbs.FirstOrDefault(x => x.DataObject == preferredDbObj.Value);
                if (matchingPrimary is not null)
                    db = matchingPrimary;
            }
        }
        if (db is null && !isLinkMember)
        {
            var writeCandidates = task.ResourceDbs
                .Where(x => string.Equals(x.Access, "W", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (writeCandidates.Count == 1)
                db = writeCandidates[0];
        }
        var access = db?.Access ?? "";
        var cache = db?.Cache == true;
        var preferAllowRowLockingOnly =
            string.Equals(access, "W", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.CacheStrategy, "T", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionMode, "P", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(task.TransactionBegin, "N", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(task.TransactionBegin, "L", StringComparison.OrdinalIgnoreCase));
        var preferCachedWritablePrimary =
            !isLinkMember &&
            effectivePrimaryObj.HasValue &&
            dbObj == effectivePrimaryObj.Value &&
            string.Equals(access, "W", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TaskType, "O", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.CacheStrategy, "D", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(task.TransactionBegin, "P", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(task.TransactionBegin, "T", StringComparison.OrdinalIgnoreCase));

        if (string.Equals(access, "R", StringComparison.OrdinalIgnoreCase))
            initParts.Add("ReadOnly = true");
        else if (isWriteRelationTarget && string.Equals(access, "W", StringComparison.OrdinalIgnoreCase))
            initParts.Add("AllowRowLocking = true");
        else if (preferAllowRowLockingOnly)
            initParts.Add("AllowRowLocking = true");
        else if (preferCachedWritablePrimary)
        {
            initParts.Add("AllowRowLocking = true");
            initParts.Add("Cached = true");
        }
        else if (taskWantsImplicitCachedWriteModel && cache && string.Equals(access, "W", StringComparison.OrdinalIgnoreCase))
            initParts.Add("Cached = true");
        else if (string.Equals(access, "W", StringComparison.OrdinalIgnoreCase))
            initParts.Add("AllowRowLocking = true");
        else if (cache)
            initParts.Add("Cached = true");

        if (cache && task.Resident)
            initParts.Add("KeepCacheAliveAfterExit = true");

        if (initParts.Count == 0)
            return "";
        return $" {{ {string.Join(", ", initParts)} }}";
    }

    private static TaskResourceColumnDef? ResolveTaskResourceColumn(TaskSemantic task, int columnReference)
    {
        task.ResourcesSemantic.ById.TryGetValue(columnReference, out var byId);
        if (byId is not null)
            return byId;
        if (columnReference > 0 && columnReference <= task.ResourcesSemantic.Ordered.Count)
            return task.ResourcesSemantic.Ordered[columnReference - 1];
        return null;
    }
}
