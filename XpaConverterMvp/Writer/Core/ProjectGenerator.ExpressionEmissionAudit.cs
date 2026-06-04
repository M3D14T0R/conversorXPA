using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static long _expressionEmissionAuditAttemptCount;
    private static long _expressionEmissionAuditPassCount;
    private static long _expressionEmissionAuditFailCount;
    private static long _expressionEmissionAuditCacheHitCount;
    private static readonly ConcurrentDictionary<string, long> _expressionEmissionAuditBySink = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _expressionEmissionAuditBySinkExpected = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _expressionEmissionAuditByFailureReason = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _expressionEmissionAuditBySourceKind = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _expressionEmissionAuditDetails = new();
    private static long _expressionEmissionAuditDetailCount;
    private const int ExpressionEmissionAuditDetailLimit = 500;

    private static void ResetExpressionEmissionAuditCounters()
    {
        Interlocked.Exchange(ref _expressionEmissionAuditAttemptCount, 0);
        Interlocked.Exchange(ref _expressionEmissionAuditPassCount, 0);
        Interlocked.Exchange(ref _expressionEmissionAuditFailCount, 0);
        Interlocked.Exchange(ref _expressionEmissionAuditCacheHitCount, 0);
        Interlocked.Exchange(ref _expressionEmissionAuditDetailCount, 0);
        _expressionEmissionAuditBySink.Clear();
        _expressionEmissionAuditBySinkExpected.Clear();
        _expressionEmissionAuditByFailureReason.Clear();
        _expressionEmissionAuditBySourceKind.Clear();
        while (_expressionEmissionAuditDetails.TryDequeue(out _))
        {
        }
    }

    private static void TrackExpressionEmissionAuditCacheHit(ExpressionEmissionContext context, string expectedReturnType, bool success)
    {
        Interlocked.Increment(ref _expressionEmissionAuditAttemptCount);
        Interlocked.Increment(ref _expressionEmissionAuditCacheHitCount);
        if (success)
            Interlocked.Increment(ref _expressionEmissionAuditPassCount);
        else
            Interlocked.Increment(ref _expressionEmissionAuditFailCount);

        TrackExpressionEmissionAuditCounters(
            context,
            expectedReturnType,
            success ? "pass" : "cache-hit-fail",
            "",
            "CacheHit");
    }

    private static void TrackExpressionEmissionAudit(
        TaskSemantic task,
        ExpressionEmissionContext context,
        string expression,
        string expectedReturnType,
        bool success,
        string sourceReturnType,
        string sourceKind,
        string failureReason)
    {
        Interlocked.Increment(ref _expressionEmissionAuditAttemptCount);
        if (success)
            Interlocked.Increment(ref _expressionEmissionAuditPassCount);
        else
            Interlocked.Increment(ref _expressionEmissionAuditFailCount);

        TrackExpressionEmissionAuditCounters(
            context,
            expectedReturnType,
            success ? "pass" : failureReason,
            sourceReturnType,
            sourceKind);

        if (success && !IsHighValueExpressionEmissionAuditSink(context.SinkKind))
            return;

        var detailIndex = Interlocked.Increment(ref _expressionEmissionAuditDetailCount);
        if (detailIndex > ExpressionEmissionAuditDetailLimit)
            return;

        var source = string.IsNullOrWhiteSpace(sourceReturnType) ? "" : sourceReturnType;
        var kind = string.IsNullOrWhiteSpace(sourceKind) ? "" : sourceKind;
        var reason = success ? "pass" : failureReason;
        _expressionEmissionAuditDetails.Enqueue(
            string.Create(
                CultureInfo.InvariantCulture,
                $"task={task.Ordinal} sink={context.SinkKind} expected={QuoteTelemetry(expectedReturnType)} source={QuoteTelemetry(source)} sourceKind={QuoteTelemetry(kind)} result={QuoteTelemetry(reason)} target={QuoteTelemetry(context.TargetInfo?.TargetMember ?? "")} expr={QuoteTelemetry(TruncateTelemetryValue(expression))}"));
    }

    private static void TrackExpressionEmissionAuditCounters(
        ExpressionEmissionContext context,
        string expectedReturnType,
        string result,
        string sourceReturnType,
        string sourceKind)
    {
        var sink = context.SinkKind.ToString();
        _expressionEmissionAuditBySink.AddOrUpdate(sink, 1, (_, value) => value + 1);

        var normalizedExpected = string.IsNullOrWhiteSpace(expectedReturnType) ? "<none>" : expectedReturnType;
        _expressionEmissionAuditBySinkExpected.AddOrUpdate(
            string.Create(CultureInfo.InvariantCulture, $"{sink}|{normalizedExpected}"),
            1,
            (_, value) => value + 1);

        if (!string.IsNullOrWhiteSpace(result) && !string.Equals(result, "pass", StringComparison.Ordinal))
            _expressionEmissionAuditByFailureReason.AddOrUpdate(
                string.Create(CultureInfo.InvariantCulture, $"{sink}|{result}"),
                1,
                (_, value) => value + 1);

        if (!string.IsNullOrWhiteSpace(sourceKind))
            _expressionEmissionAuditBySourceKind.AddOrUpdate(
                string.Create(CultureInfo.InvariantCulture, $"{sink}|{sourceKind}|{sourceReturnType}"),
                1,
                (_, value) => value + 1);
    }

    private static bool IsHighValueExpressionEmissionAuditSink(ExpressionSinkKind sinkKind)
        => sinkKind is ExpressionSinkKind.Assignment
            or ExpressionSinkKind.RunArgument
            or ExpressionSinkKind.BindValue
            or ExpressionSinkKind.FilterComparison
            or ExpressionSinkKind.ViewBinding
            or ExpressionSinkKind.SqlExpression
            or ExpressionSinkKind.CallArgument;

    private static string ResolveStrictEmissionFailureReason(
        bool hasReliableSource,
        string expectedReturnType,
        string sourceReturnType)
    {
        if (!hasReliableSource)
            return "missing-source-type";

        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return "strict-engine-rejected";

        if (string.IsNullOrWhiteSpace(sourceReturnType))
            return "missing-source-return-type";

        return "unsupported-bridge";
    }

    private static void LogExpressionEmissionAuditSummary()
    {
        var attempts = Interlocked.Read(ref _expressionEmissionAuditAttemptCount);
        var passes = Interlocked.Read(ref _expressionEmissionAuditPassCount);
        var fails = Interlocked.Read(ref _expressionEmissionAuditFailCount);
        var cacheHits = Interlocked.Read(ref _expressionEmissionAuditCacheHitCount);

        ConversionTelemetry.Log(
            "EXPR_EMIT_AUDIT",
            string.Create(
                CultureInfo.InvariantCulture,
                $"summary attempts={attempts} passes={passes} fails={fails} cacheHits={cacheHits}"));

        if (!_expressionEmissionAuditBySink.IsEmpty)
            ConversionTelemetry.Log("EXPR_EMIT_AUDIT", $"sinks {FormatExpressionEmissionAuditCounts(_expressionEmissionAuditBySink)}");

        if (!_expressionEmissionAuditBySinkExpected.IsEmpty)
            ConversionTelemetry.Log("EXPR_EMIT_AUDIT", $"sink-expected {FormatExpressionEmissionAuditCounts(_expressionEmissionAuditBySinkExpected)}");

        if (!_expressionEmissionAuditByFailureReason.IsEmpty)
            ConversionTelemetry.Log("EXPR_EMIT_AUDIT", $"failures {FormatExpressionEmissionAuditCounts(_expressionEmissionAuditByFailureReason)}");

        if (!_expressionEmissionAuditBySourceKind.IsEmpty)
            ConversionTelemetry.Log("EXPR_EMIT_AUDIT", $"source-kind {FormatExpressionEmissionAuditCounts(_expressionEmissionAuditBySourceKind)}");

        if (_expressionEmissionAuditDetails.IsEmpty)
            return;

        var index = 1;
        foreach (var detail in _expressionEmissionAuditDetails)
        {
            ConversionTelemetry.Log(
                "EXPR_EMIT_AUDIT_DETAIL",
                string.Create(CultureInfo.InvariantCulture, $"{index:000000} {detail}"));
            index++;
        }
    }

    private static string FormatExpressionEmissionAuditCounts(ConcurrentDictionary<string, long> counts)
        => string.Join(
            ",",
            counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value}")));
}
