using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static long _legacyExpressionTreatmentCount;
    private static readonly ConcurrentDictionary<string, long> _legacyExpressionTreatmentCountByMethod = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _legacyExpressionTypingDetails = new();
    private static long _legacyExpressionTypingDetailCount;
    private static long _newLegacyExpressionTreatmentCount;
    private static readonly ConcurrentDictionary<string, long> _newLegacyExpressionTreatmentCountByMethod = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _newLegacyExpressionDetails = new();
    private static long _newLegacyExpressionDetailCount;
    private static long _newLegacyCleanupTreatmentCount;
    private static readonly ConcurrentDictionary<string, long> _newLegacyCleanupTreatmentCountByMethod = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _newLegacyCleanupDetails = new();
    private static long _newLegacyCleanupDetailCount;
    private const int NewLegacyExpressionDetailLimit = 500;
    private const int NewLegacyCleanupDetailLimit = 500;

    private static void TrackLegacyExpressionTreatment(string area, string method)
    {
        return;
    }

    private static void TrackLegacyExpressionTreatment(string area, string method, string detail)
    {
        return;
    }

    private static string TrackLegacyExpressionTreatmentIfChanged(
        string area,
        string method,
        string original,
        string rewritten,
        string? detail = null)
    {
        if (!string.Equals(original, rewritten, StringComparison.Ordinal) &&
            !AreTelemetryEquivalentExpressionForms(original, rewritten))
        {
            TrackLegacyExpressionTreatment(
                area,
                method,
                string.IsNullOrWhiteSpace(detail)
                    ? string.Create(CultureInfo.InvariantCulture, $"from={original} to={rewritten}")
                    : detail);
        }

        return rewritten;
    }

    private static void TrackNewLegacyExpressionTreatment(string area, string method)
    {
        TrackNewLegacyExpressionTreatment(area, method, "");
    }

    private static void TrackNewLegacyExpressionTreatment(string area, string method, string detail)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(method))
            return;

        var key = $"{area}.{method}";
        Interlocked.Increment(ref _newLegacyExpressionTreatmentCount);
        _newLegacyExpressionTreatmentCountByMethod.AddOrUpdate(key, 1, (_, value) => value + 1);

        if (string.IsNullOrWhiteSpace(detail))
            return;

        var detailIndex = Interlocked.Increment(ref _newLegacyExpressionDetailCount);
        if (detailIndex <= NewLegacyExpressionDetailLimit)
            _newLegacyExpressionDetails.Enqueue($"{area}.{method} {NormalizeLegacyExpressionTelemetryDetail(detail)}");
    }

    private static string TrackNewLegacyExpressionTreatmentIfChanged(
        string area,
        string method,
        string original,
        string rewritten,
        string? detail = null)
    {
        if (!string.Equals(original, rewritten, StringComparison.Ordinal) &&
            !AreTelemetryEquivalentExpressionForms(original, rewritten))
        {
            TrackNewLegacyExpressionTreatment(
                area,
                method,
                string.IsNullOrWhiteSpace(detail)
                    ? string.Create(CultureInfo.InvariantCulture, $"from={original} to={rewritten}")
                    : detail);
        }

        return rewritten;
    }

    private static string TrackNewLegacyExpressionBridgeIfChanged(
        string area,
        string method,
        string original,
        string rewritten,
        string? detail = null)
    {
        if (!string.Equals(original, rewritten, StringComparison.Ordinal) &&
            !AreTelemetryEquivalentExpressionForms(original, rewritten) &&
            HasNewLegacyExpressionBridgeSignal(original, rewritten))
        {
            TrackNewLegacyExpressionTreatment(
                area,
                method,
                string.IsNullOrWhiteSpace(detail)
                    ? string.Create(CultureInfo.InvariantCulture, $"from={original} to={rewritten}")
                    : detail);
        }

        return rewritten;
    }

    private static string TrackNewLegacyCleanupTreatmentIfChanged(
        string area,
        string method,
        string original,
        string rewritten,
        string? detail = null)
    {
        if (!string.Equals(original, rewritten, StringComparison.Ordinal) &&
            !AreTelemetryEquivalentExpressionForms(original, rewritten))
        {
            TrackNewLegacyCleanupTreatment(
                area,
                method,
                string.IsNullOrWhiteSpace(detail)
                    ? string.Create(CultureInfo.InvariantCulture, $"from={original} to={rewritten}")
                    : detail);
        }

        return rewritten;
    }

    private static void TrackNewLegacyCleanupTreatment(string area, string method)
    {
        TrackNewLegacyCleanupTreatment(area, method, "");
    }

    private static void TrackNewLegacyCleanupTreatment(string area, string method, string detail)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(method))
            return;

        if (string.Equals(area, "GeneratedCleanup", StringComparison.Ordinal) &&
            !IsGeneratedCleanupTypingTreatment(method))
            return;

        var key = $"{area}.{method}";
        Interlocked.Increment(ref _newLegacyCleanupTreatmentCount);
        _newLegacyCleanupTreatmentCountByMethod.AddOrUpdate(key, 1, (_, value) => value + 1);

        if (string.IsNullOrWhiteSpace(detail))
            return;

        var detailIndex = Interlocked.Increment(ref _newLegacyCleanupDetailCount);
        if (detailIndex <= NewLegacyCleanupDetailLimit)
            _newLegacyCleanupDetails.Enqueue($"{area}.{method} {NormalizeLegacyExpressionTelemetryDetail(detail)}");
    }

    private static bool HasNewLegacyExpressionBridgeSignal(string original, string rewritten)
    {
        foreach (var marker in NewLegacyExpressionBridgeMarkers)
        {
            if (CountOrdinalOccurrences(original, marker) != CountOrdinalOccurrences(rewritten, marker))
                return true;
        }

        return false;
    }

    private static readonly string[] NewLegacyExpressionBridgeMarkers =
    {
        "u.CastToText(",
        "u.CastToNumber(",
        "u.CastToDate(",
        "u.CastToTime(",
        "u.CastToBool(",
        "u.CastToByteArray(",
        "UserMethods.ToTime(",
        "UserMethods.ToDate(",
        "ByteArrayToText(",
        "ToByteArray(",
        "new System.IntPtr",
        "(object)",
        "XPARuntimeCore.Box.Date.Empty"
    };

    private static int CountOrdinalOccurrences(string value, string marker)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(marker))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static bool AreTelemetryEquivalentExpressionForms(string original, string rewritten)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(rewritten))
            return false;

        var normalizedOriginal = NormalizeExpressionFormForLegacyTelemetry(original);
        var normalizedRewritten = NormalizeExpressionFormForLegacyTelemetry(rewritten);
        return string.Equals(normalizedOriginal, normalizedRewritten, StringComparison.Ordinal);
    }

    private static string NormalizeExpressionFormForLegacyTelemetry(string expression)
    {
        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        var result = new System.Text.StringBuilder(trimmed.Length);
        var inString = false;
        var escaping = false;
        var pendingWhitespace = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (inString)
            {
                result.Append(ch);
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                if (pendingWhitespace && result.Length > 0)
                    result.Append(' ');
                pendingWhitespace = false;
                inString = true;
                result.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace =
                    result.Length > 0 &&
                    result[^1] != ',' &&
                    result[^1] != '(';
                continue;
            }

            if (ch == ',' || ch == '(' || ch == ')' || ch == '<' || ch == '>' || ch == '=' || ch == '!')
            {
                pendingWhitespace = false;
                result.Append(ch);
                continue;
            }

            if (pendingWhitespace)
            {
                if (result.Length > 0 &&
                    result[^1] != ',' &&
                    result[^1] != '(')
                    result.Append(' ');
                pendingWhitespace = false;
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    private static void ResetLegacyExpressionTelemetryCounters()
    {
        Interlocked.Exchange(ref _legacyExpressionTreatmentCount, 0);
        _legacyExpressionTreatmentCountByMethod.Clear();
        Interlocked.Exchange(ref _legacyExpressionTypingDetailCount, 0);
        while (_legacyExpressionTypingDetails.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _newLegacyExpressionTreatmentCount, 0);
        _newLegacyExpressionTreatmentCountByMethod.Clear();
        Interlocked.Exchange(ref _newLegacyExpressionDetailCount, 0);
        while (_newLegacyExpressionDetails.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _newLegacyCleanupTreatmentCount, 0);
        _newLegacyCleanupTreatmentCountByMethod.Clear();
        Interlocked.Exchange(ref _newLegacyCleanupDetailCount, 0);
        while (_newLegacyCleanupDetails.TryDequeue(out _))
        {
        }
    }

    private static void LogLegacyExpressionTelemetrySummary()
    {
        ConversionTelemetry.Log("LEGACY_EXPR_TYPING", "summary calls=0 methods=0");
        ConversionTelemetry.Log("LEGACY_EXPR_TYPING_LIST", "summary calls=0");
        LogNewLegacyExpressionTelemetrySummary();
        LogNewLegacyCleanupTelemetrySummary();
    }

    private static void LogNewLegacyExpressionTelemetrySummary()
    {
        var snapshot = _newLegacyExpressionTreatmentCountByMethod.ToArray();
        if (snapshot.Length == 0)
        {
            ConversionTelemetry.Log("NEW_LEGACY_EXPR", "summary calls=0 methods=0");
            ConversionTelemetry.Log("NEW_LEGACY_EXPR_LIST", "summary calls=0");
            return;
        }

        LogLegacyExpressionTelemetryCounts("NEW_LEGACY_EXPR", snapshot);
        LogNewLegacyExpressionExpandedList(snapshot);
        LogNewLegacyExpressionDetails();
    }

    private static void LogNewLegacyCleanupTelemetrySummary()
    {
        var snapshot = _newLegacyCleanupTreatmentCountByMethod.ToArray();
        if (snapshot.Length == 0)
        {
            ConversionTelemetry.Log("NEW_LEGACY_CLEANUP", "summary calls=0 methods=0");
            ConversionTelemetry.Log("NEW_LEGACY_CLEANUP_LIST", "summary calls=0");
            return;
        }

        LogLegacyExpressionTelemetryCounts("NEW_LEGACY_CLEANUP", snapshot);
        LogNewLegacyCleanupExpandedList(snapshot);
        LogNewLegacyCleanupDetails();
    }

    private static void LogLegacyExpressionTelemetryCounts(
        string telemetryCategory,
        IEnumerable<KeyValuePair<string, long>> counts)
    {
        var snapshot = counts.ToArray();
        var total = snapshot.Sum(kv => kv.Value);

        ConversionTelemetry.Log(
            telemetryCategory,
            string.Create(CultureInfo.InvariantCulture, $"summary calls={total} methods={snapshot.Length}"));

        foreach (var areaGroup in snapshot
                     .GroupBy(kv => ResolveLegacyExpressionTelemetryArea(kv.Key))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var methods = string.Join(
                ",",
                areaGroup
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => string.Create(CultureInfo.InvariantCulture, $"{ResolveLegacyExpressionTelemetryMethod(kv.Key)}:{kv.Value}")));
            ConversionTelemetry.Log(telemetryCategory, $"{areaGroup.Key} {methods}");
        }
    }

    private static void LogLegacyExpressionTypingExpandedList(IReadOnlyCollection<KeyValuePair<string, long>> counts)
    {
        var total = counts.Sum(kv => kv.Value);
        ConversionTelemetry.Log(
            "LEGACY_EXPR_TYPING_LIST",
            string.Create(CultureInfo.InvariantCulture, $"summary calls={total}"));

        var index = 1;
        foreach (var item in counts
                     .OrderBy(kv => ResolveLegacyExpressionTelemetryArea(kv.Key), StringComparer.Ordinal)
                     .ThenBy(kv => ResolveLegacyExpressionTelemetryMethod(kv.Key), StringComparer.Ordinal))
        {
            for (var occurrence = 0; occurrence < item.Value; occurrence++)
            {
                ConversionTelemetry.Log(
                    "LEGACY_EXPR_TYPING_LIST",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{index:000000} {ResolveLegacyExpressionTelemetryArea(item.Key)}.{ResolveLegacyExpressionTelemetryMethod(item.Key)}"));
                index++;
            }
        }
    }

    private static void LogLegacyExpressionTypingDetails()
    {
        if (_legacyExpressionTypingDetails.IsEmpty)
            return;

        ConversionTelemetry.Log("LEGACY_EXPR_TYPING_DETAIL", "summary");
        var index = 1;
        foreach (var detail in _legacyExpressionTypingDetails)
        {
            ConversionTelemetry.Log(
                "LEGACY_EXPR_TYPING_DETAIL",
                string.Create(CultureInfo.InvariantCulture, $"{index:000000} {detail}"));
            index++;
        }
    }

    private static void LogNewLegacyExpressionExpandedList(IReadOnlyCollection<KeyValuePair<string, long>> counts)
    {
        var total = counts.Sum(kv => kv.Value);
        ConversionTelemetry.Log(
            "NEW_LEGACY_EXPR_LIST",
            string.Create(CultureInfo.InvariantCulture, $"summary calls={total}"));

        foreach (var item in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ConversionTelemetry.Log(
                "NEW_LEGACY_EXPR_LIST",
                string.Create(CultureInfo.InvariantCulture, $"{item.Key}:{item.Value}"));
        }
    }

    private static void LogNewLegacyExpressionDetails()
    {
        if (_newLegacyExpressionDetails.IsEmpty)
            return;

        ConversionTelemetry.Log(
            "NEW_LEGACY_EXPR_DETAIL",
            string.Create(CultureInfo.InvariantCulture, $"summary sampled={_newLegacyExpressionDetails.Count} total={_newLegacyExpressionDetailCount}"));

        var index = 1;
        foreach (var detail in _newLegacyExpressionDetails)
        {
            ConversionTelemetry.Log(
                "NEW_LEGACY_EXPR_DETAIL",
                string.Create(CultureInfo.InvariantCulture, $"{index:000000} {detail}"));
            index++;
        }
    }

    private static void LogNewLegacyCleanupExpandedList(IReadOnlyCollection<KeyValuePair<string, long>> counts)
    {
        var total = counts.Sum(kv => kv.Value);
        ConversionTelemetry.Log(
            "NEW_LEGACY_CLEANUP_LIST",
            string.Create(CultureInfo.InvariantCulture, $"summary calls={total}"));

        foreach (var item in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ConversionTelemetry.Log(
                "NEW_LEGACY_CLEANUP_LIST",
                string.Create(CultureInfo.InvariantCulture, $"{item.Key}:{item.Value}"));
        }
    }

    private static void LogNewLegacyCleanupDetails()
    {
        if (_newLegacyCleanupDetails.IsEmpty)
            return;

        ConversionTelemetry.Log(
            "NEW_LEGACY_CLEANUP_DETAIL",
            string.Create(CultureInfo.InvariantCulture, $"summary sampled={_newLegacyCleanupDetails.Count} total={_newLegacyCleanupDetailCount}"));

        var index = 1;
        foreach (var detail in _newLegacyCleanupDetails)
        {
            ConversionTelemetry.Log(
                "NEW_LEGACY_CLEANUP_DETAIL",
                string.Create(CultureInfo.InvariantCulture, $"{index:000000} {detail}"));
            index++;
        }
    }

    private static string NormalizeLegacyExpressionTelemetryDetail(string detail)
    {
        var normalized = detail
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }

    private static bool IsLegacyExpressionTypingTreatment(string key)
    {
        var area = ResolveLegacyExpressionTelemetryArea(key);
        if (string.Equals(area, "TypeInference", StringComparison.Ordinal) ||
            string.Equals(area, "NormalizeType", StringComparison.Ordinal) ||
            string.Equals(area, "Context", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(area, "GeneratedCleanup", StringComparison.Ordinal) &&
               IsGeneratedCleanupTypingTreatment(ResolveLegacyExpressionTelemetryMethod(key));
    }

    private static bool IsGeneratedCleanupTypingTreatment(string method)
    {
        if (string.Equals(method, "NormalizeGeneratedTaskCode", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeGeneratedCode", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeStandaloneParenthesizedInvocationStatements", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeGeneratedTaskCounterReferences", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeGeneratedTaskLocalModelReferences", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeGeneratedTaskParentIoReferences", StringComparison.Ordinal) ||
            string.Equals(method, "NormalizeGeneratedTaskBindValueScalarCasts", StringComparison.Ordinal))
        {
            return false;
        }

        return method.Contains("BindValue", StringComparison.Ordinal) ||
               method.Contains("Cast", StringComparison.Ordinal) ||
               method.Contains("Comparison", StringComparison.Ordinal) ||
               method.Contains("Scalar", StringComparison.Ordinal) ||
               method.Contains("DateTime", StringComparison.Ordinal) ||
               method.Contains("TimeColumn", StringComparison.Ordinal) ||
               method.Contains("RedundantTimeFormatting", StringComparison.Ordinal) ||
               method.Contains("NullComparison", StringComparison.Ordinal) ||
               method.Contains("TextLiteralGetParam", StringComparison.Ordinal) ||
               method.Contains("Numeric", StringComparison.Ordinal) ||
               method.Contains("Bool", StringComparison.Ordinal) ||
               method.Contains("ByteArray", StringComparison.Ordinal) ||
               method.Contains("Blob", StringComparison.Ordinal);
    }

    private static string ResolveLegacyExpressionTelemetryArea(string key)
    {
        var dot = key.IndexOf('.');
        return dot <= 0 ? "Unknown" : key[..dot];
    }

    private static string ResolveLegacyExpressionTelemetryMethod(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 || dot + 1 >= key.Length ? key : key[(dot + 1)..];
    }
}
