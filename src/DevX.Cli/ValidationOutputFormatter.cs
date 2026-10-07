using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Domain;
using DevX.Protocol;

namespace DevX.Cli;

public static class ValidationOutputFormatter
{
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
