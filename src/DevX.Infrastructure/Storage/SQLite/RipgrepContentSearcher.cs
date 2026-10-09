using System.Diagnostics;

namespace DevX.Infrastructure.Storage.SQLite;

/// <summary>
/// `devx search text` through ripgrep when it is on PATH, with the in-process scan in
/// <see cref="SqliteStorageInitializer"/> as the fallback. The output shape is identical
/// either way -- path, line, and the matching line's text -- so nothing downstream, and no
/// `--format json` payload, depends on which engine answered.
///
/// ripgrep is used because it is the regex engine the owner asked for AND it respects
/// .gitignore, which the in-process scan cannot: that scan reads every indexed file from
/// disk and matches a literal substring, which is why agents had to guess word after word
/// (25 of 111 `search text` calls in the 1.6.11 transcripts came back empty).
/// </summary>
public static class RipgrepContentSearcher
{
    /// <summary>Enough matches to put the wanted ones in the first `limit` after ordering.
    /// A pattern common enough to exceed this is one the agent must narrow anyway.</summary>
    private const int ScanCeiling = 5000;

    /// <summary>Longest preview kept. Same idea as the 120-char cut the agent formatter makes;
    /// this only bounds memory for minified or single-line files.</summary>
    private const int PreviewMaxLength = 400;

    public sealed record Hit(string Path, int Line, string Text);

    /// <summary>
    /// Search <paramref name="rootPath"/> with ripgrep. False means "no answer from ripgrep" --
    /// not installed, or the pattern is not a valid regex -- and the caller must fall back to
    /// the in-process scan rather than report no matches.
    /// </summary>
    public static bool TrySearch(string rootPath, string query, out List<Hit> hits, CancellationToken cancellationToken = default)
    {
        hits = [];
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return false;
        }

        // Case-insensitive, like the in-process scan it replaces: `devx search text IsEnabled`
        // has always found `isenabled`, and an agent that has read that behaviour must not have
        // it taken away. A caller who wants case-sensitivity writes it into the regex.
        if (TryRun(rootPath, ["-i"], query, hits, cancellationToken))
        {
            return true;                              // possibly zero hits: that is still an answer
        }

        // rg exits 2 on a pattern that will not compile -- `GetValueOrDefault(` is a query
        // agents really typed. Retry as fixed strings so a literal-looking query still works.
        hits.Clear();
        return TryRun(rootPath, ["-i", "-F"], query, hits, cancellationToken);
    }

    private static bool TryRun(string rootPath, string[] flags, string query, List<Hit> hits, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("rg")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var flag in flags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("--line-number");
        start.ArgumentList.Add("--no-heading");
        start.ArgumentList.Add("--with-filename");
        start.ArgumentList.Add("--color=never");
        start.ArgumentList.Add("--no-messages");
        start.ArgumentList.Add("--");                 // everything after this is the pattern
        start.ArgumentList.Add(query);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;                              // rg is not installed
        }

        if (process is null)
        {
            return false;
        }

        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            var truncated = false;
            try
            {
                string? line;
                while ((line = process.StandardOutput.ReadLine()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryParse(line, out var hit))
                    {
                        hits.Add(hit);
                        if (hits.Count >= ScanCeiling)
                        {
                            truncated = true;          // bounded: a common word must not stream forever
                            break;
                        }
                    }
                }

                process.WaitForExit(30_000);
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }

            if (truncated)
            {
                // We stopped reading, so the exit code measures the kill, not ripgrep's verdict.
                return true;
            }

            // Exit 1 is "no matches", which is an answer. Exit 2 is an error -- a pattern that
            // will not compile, an unreadable root -- and only then does the caller fall back.
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode is 0 or 1;
        }
    }

    /// <summary>`path:line:text`. The path may contain a colon (a checkout under a directory
    /// with one in its name), so the line number is found as the first all-digits field after
    /// the last colon that still parses -- ripgrep itself never quotes or escapes it.</summary>
    private static bool TryParse(string line, out Hit hit)
    {
        hit = new Hit(string.Empty, 0, string.Empty);
        var first = line.IndexOf(':');
        if (first <= 0)
        {
            return false;
        }

        var second = line.IndexOf(':', first + 1);
        while (second > 0 && !int.TryParse(line.AsSpan(first + 1, second - first - 1), out _))
        {
            // A colon inside the path: keep looking for the field that is really the line number.
            first = second;
            second = line.IndexOf(':', first + 1);
        }

        if (second <= first || !int.TryParse(line.AsSpan(first + 1, second - first - 1), out var number))
        {
            return false;
        }

        var text = line[(second + 1)..].Trim();
        if (text.Length > PreviewMaxLength) text = text[..PreviewMaxLength];
        hit = new Hit(line[..first], number, text);
        return true;
    }
}
