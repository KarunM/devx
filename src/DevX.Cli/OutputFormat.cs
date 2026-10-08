using System.Collections;

namespace DevX.Cli;

/// <summary>
/// Which output format the CLI uses when the caller does not name one (1.6.7).
///
/// Before this, the default was always <c>text</c>, and that was the whole cost gap between
/// DevX and a plain shell: the benchmark arm calls the CLI without <c>--format</c>, so
/// <c>devx search Async</c> handed it 110,795 bytes where the agent format returns 926. An
/// agent is on the other end of a pipe far more often than a person is, so the lean format
/// is now the default whenever the output is not a terminal.
///
/// This is deliberately pure: the caller passes what it observed about its environment, so
/// the rule can be tested for every combination without a real pty.
/// </summary>
public static class OutputFormat
{
    public const string Text = "text";
    public const string Agent = "agent";

    /// <summary>
    /// The format to use. An explicit <c>--format</c> always wins and is byte-identical to
    /// the pre-1.6.7 behaviour.
    /// </summary>
    public static string Resolve(string? explicitFormat, bool looksLikeAnAgent)
        => explicitFormat ?? (looksLikeAnAgent ? Agent : Text);

    /// <summary>
    /// True when the caller is an agent rather than a person. A pipe is the reliable signal;
    /// the env vars are the belt to that braces, for harnesses that allocate a pty (where a
    /// redirect test would see a terminal) but still set their own marker.
    /// </summary>
    public static bool LooksLikeAnAgent(IDictionary environment, bool outputRedirected)
    {
        foreach (var name in environment.Keys)
        {
            var key = name as string ?? string.Empty;
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CURSOR_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("AIDER_", StringComparison.OrdinalIgnoreCase)
                || key is "AGENT" or "AI_AGENT")
            {
                return true;
            }
        }

        return outputRedirected;
    }

    /// <summary>The same rule against this process's real environment.</summary>
    public static bool LooksLikeAnAgent()
        => LooksLikeAnAgent(Environment.GetEnvironmentVariables(), Console.IsOutputRedirected);
}
