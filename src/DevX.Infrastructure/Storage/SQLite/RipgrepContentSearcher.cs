using System.Diagnostics;

namespace DevX.Infrastructure.Storage.SQLite;

/// <summary>
/// `devx search text` through ripgrep -- configured, bundled with DevX, or on PATH, in that
/// order (<see cref="RipgrepLocator"/>) -- with the in-process scan in
/// <see cref="SqliteStorageInitializer"/> as the fallback. The output shape is identical
/// either way -- path, line, and the matching line's text -- so nothing downstream, and no
/// `--format json` payload, depends on which engine answered.
///
/// ripgrep is used because it is a full regex engine AND it respects
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
    /// <summary>
    /// The binary is resolved by <see cref="RipgrepLocator"/>: the repository's configured
    /// `rg.path`, then the copy bundled with DevX, then PATH. Null means no ripgrep anywhere.
    /// </summary>
    public static bool TrySearch(string rootPath, string query, out List<Hit> hits, CancellationToken cancellationToken = default)
        => TrySearch(rootPath, query, out hits, out _, cancellationToken);

    public static bool TrySearch(string rootPath, string query, out List<Hit> hits, out bool scanCapped, CancellationToken cancellationToken = default)
        => TrySearch(rootPath, query, RipgrepLocator.Locate(rootPath).Executable, out hits, out scanCapped, cancellationToken);

    public static bool TrySearch(string rootPath, string query, string? executable, out List<Hit> hits, CancellationToken cancellationToken = default)
        => TrySearch(rootPath, query, executable, out hits, out _, cancellationToken);

    public static bool TrySearch(string rootPath, string query, string? executable, out List<Hit> hits, out bool scanCapped, CancellationToken cancellationToken = default)
        => TrySearch(rootPath, query, executable, out hits, out scanCapped, TimeSpan.FromSeconds(30), cancellationToken);

    public static bool TrySearch(string rootPath, string query, string? executable, out List<Hit> hits, out bool scanCapped, TimeSpan processTimeout, CancellationToken cancellationToken = default)
    {
        hits = [];
        scanCapped = false;
        if (string.IsNullOrWhiteSpace(executable)
            || string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return false;
        }

        // Case-insensitive, like the in-process scan it replaces: `devx search text IsEnabled`
        // has always found `isenabled`, and an agent that has read that behaviour must not have
        // it taken away. A caller who wants case-sensitivity writes it into the regex.
        if (TryRun(executable, rootPath, ["-i"], query, hits, out scanCapped, processTimeout, cancellationToken))
        {
            return true;                              // possibly zero hits: that is still an answer
        }

        // rg exits 2 on a pattern that will not compile -- `GetValueOrDefault(` is a query
        // agents really typed. Retry as fixed strings so a literal-looking query still works.
        hits.Clear();
        return TryRun(executable, rootPath, ["-i", "-F"], query, hits, out scanCapped, processTimeout, cancellationToken);
    }

    private static bool TryRun(string executable, string rootPath, string[] flags, string query, List<Hit> hits, out bool scanCapped, TimeSpan processTimeout, CancellationToken cancellationToken)
    {
        scanCapped = false;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var flag in flags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("--line-number");
        start.ArgumentList.Add("--no-heading");
        start.ArgumentList.Add("--with-filename");
        start.ArgumentList.Add("--color=never");
        start.ArgumentList.Add("--no-messages");
        // Hidden files (.github/workflows) are tracked source; DevX's own state and git
        // internals are not.
        start.ArgumentList.Add("--hidden");
        start.ArgumentList.Add("--glob");
        start.ArgumentList.Add("!.git");
        start.ArgumentList.Add("--glob");
        start.ArgumentList.Add("!.devx");
        start.ArgumentList.Add("--");                 // everything after this is the pattern
        start.ArgumentList.Add(query);
        start.ArgumentList.Add(".");

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
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            var truncated = false;
            var timedOut = 0;
            using var timeout = new Timer(_ =>
            {
                Interlocked.Exchange(ref timedOut, 1);
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }, null, processTimeout, Timeout.InfiniteTimeSpan);
            using var cancellation = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
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
                            // Stop ripgrep now: it is blocked writing into a pipe nobody reads any more,
                            // so waiting for it to exit below would sit out the whole 30 s timeout and
                            // then report "no answer" for exactly the common names that hit the ceiling.
                            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                            break;
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref timedOut) != 0) return false;
                if (!process.WaitForExit((int)Math.Min(int.MaxValue, processTimeout.TotalMilliseconds)))
                {
                    Interlocked.Exchange(ref timedOut, 1);
                    return false;
                }
                if (Volatile.Read(ref timedOut) != 0) return false;
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already gone
                }
            }

            if (truncated)
            {
                // We stopped reading, so the exit code measures the kill, not ripgrep's verdict.
                scanCapped = true;
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
        var path = line[..first];
        if (OperatingSystem.IsWindows()) path = path.Replace('\\', '/');
        // The explicit "." search root makes ripgrep print "./a.txt"; indexed paths are root-relative.
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        hit = new Hit(path, number, text);
        return true;
    }
}
