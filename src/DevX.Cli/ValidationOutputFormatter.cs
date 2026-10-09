using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Domain;
using DevX.Indexer;
using DevX.Protocol;

namespace DevX.Cli;

public static class ValidationOutputFormatter
{
    /// <summary>`devx verify`: one line when it passed -- status, what ran, and where the full log
    /// is -- and the failing run's own `path:line: message` lines when it did not (brief OSS14
    /// adjustment, 2026-10-09: a verify that passed must not add five lines to an edit's reply).</summary>
    public static string FormatVerify(ProtocolResponse response)
    {
        var payload = response.Data?.Deserialize<VerifyExecutionResponse>();
        if (payload is null) return Format(response, "verify");
        var verification = new VerifyAfterEditResult(
            payload.Build.Execution,
            payload.Tests?.Execution,
            payload.Build.Execution.Summary.StartsWith("skip", StringComparison.OrdinalIgnoreCase));
        if (response.Success) return VerifyAfterEdit.FormatOneLine("verify", verification);
        var lines = VerifyAfterEdit.FormatVerifyLines(verification).ToList();
        if (lines.Count == 0)
        {
            var error = response.Errors.FirstOrDefault();
            lines.Add(error is null ? "FAIL verify" : $"FAIL verify — {error.Code}: {error.Message}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string Format(ProtocolResponse response, string capability)
    {
        var label = capability.Equals("test", StringComparison.OrdinalIgnoreCase) ? "tests" : capability;
        var payload = response.Data?.Deserialize<ValidationExecutionResponse>();
        if (response.Success)
        {
            return string.IsNullOrWhiteSpace(payload?.Execution.Summary)
                ? $"PASS {label}"
                : payload.Execution.Summary;
        }

        if (payload?.Execution is { } execution)
        {
            if (!string.IsNullOrWhiteSpace(execution.Summary)) return execution.Summary;
            var detail = execution.Diagnostics.FirstOrDefault();
            var headline = detail is null
                ? $"FAIL {label}"
                : $"FAIL {label} — {FormatDiagnostic(detail)}";
            return string.IsNullOrWhiteSpace(execution.RawOutput)
                ? headline
                : $"{headline}{Environment.NewLine}Log: {execution.RawOutput}";
        }

        var error = response.Errors.FirstOrDefault();
        return error is null
            ? $"FAIL {label}"
            : $"FAIL {label} — {error.Code}: {error.Message}";
    }

    private static string FormatDiagnostic(DiagnosticRecord diagnostic)
    {
        var file = diagnostic.File is null ? null : CleanFile(diagnostic.File);
        var location = file is null
            ? string.Empty
            : diagnostic.Line is null
                ? $" ({file})"
                : $" ({file}:{diagnostic.Line})";
        return $"{diagnostic.Code}: {Compact(diagnostic.Message)}{location}";
    }

    private static string Compact(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 160 ? compact : compact[..157] + "...";
    }

    private static string CleanFile(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
           ?? value;
}
