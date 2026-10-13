using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevX.Indexer;

/// <summary>
/// The `build` and `test` blocks of `.devx/config.json`.
///
/// Auto-discovery is DevX guessing how a repository is built. This file is the repository
/// answering for itself, so it is consulted first and wins. It exists because guessing fails
/// on real projects: Django runs its suite through `tests/runtests.py &lt;label&gt;`, not through
/// pytest or `dotnet test`, and an agent whose `test` call cannot run the tests writes its own
/// runner instead -- which is what the LG1 transcript did.
/// </summary>
public sealed record DevXCommandConfig
{
    public const string RelativePath = ".devx/config.json";

    public static string PathFor(string root) => Path.Combine(root, ".devx", "config.json");

    /// <summary>
    /// Reads one block (`"build"` or `"test"`). A malformed or half-written config returns null
    /// rather than throwing: a broken config must not be able to break `devx build`.
    /// </summary>
    public static DevXCommandConfig? TryRead(string root, string section)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(section, out var block) || block.ValueKind != JsonValueKind.Object) return null;
            if (!block.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String) return null;
            var command = commandElement.GetString();
            if (string.IsNullOrWhiteSpace(command)) return null;

            // `args` present means the documented form: no shell, each argument passed as-is.
            var hasArgs = block.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array;
            var args = new List<string>();
            if (hasArgs)
            {
                foreach (var item in argsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) args.Add(item.GetString()!);
                }
            }

            var workingDirectory = block.TryGetProperty("workingDirectory", out var cwd) && cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;

            Dictionary<string, string>? environment = null;
            if (block.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
            {
                environment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in envElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String) environment[property.Name] = property.Value.GetString()!;
                }
            }

            // Opt-in only. An agent legitimately needs to install a dependency, and a tool that
            // refuses is a tool the agent works around by hand -- which is what the LG1
            // transcript did. The benchmark's no-install rule lives in its prompt, not here.
            var blockInstall = block.TryGetProperty("blockInstall", out var blockInstallElement)
                && blockInstallElement.ValueKind == JsonValueKind.True;

            return new DevXCommandConfig(command!, args, workingDirectory, environment, !hasArgs && LooksLikeShellCommandLine(command!), blockInstall);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private DevXCommandConfig(string command, IReadOnlyList<string> args, string? workingDirectory, IReadOnlyDictionary<string, string>? environment, bool isShell, bool blockInstall)
    {
        Command = command;
        Args = args;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        IsShell = isShell;
        BlockInstall = blockInstall;
        ShellLine = isShell ? command : null;
    }

    public string Command { get; }
    public IReadOnlyList<string> Args { get; }
    public string? WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>
    /// True when `command` is a whole command line (`cd tests &amp;&amp; ../.venv/bin/python
    /// runtests.py`) rather than an executable name. Configs are commonly written that way, so
    /// it has to work -- with no `args`, the string is split on whitespace unless it uses shell
    /// syntax, in which case it is run through `/bin/sh -c`.
    /// </summary>
    public bool IsShell { get; }

    /// <summary>
    /// `"blockInstall": true` in the same block. Off by default: a configured command that
    /// installs runs, because a repository's own build often has to restore its dependencies
    /// before it can build anything. DevX still never adds an install step of its own.
    /// </summary>
    public bool BlockInstall { get; }

    /// <summary>The unsplit command line, when there is one. Null for the `command` + `args` form.</summary>
    private string? ShellLine { get; }

    /// <summary>The command line with `{target}` substituted, for the log, the guard and the response.</summary>
    public string Render(string? target)
        => IsShell
            ? ShellLine!.Replace("{target}", ShellQuote(target), StringComparison.Ordinal)
            : string.Join(' ', new[] { SplitCommandLine().First() }.Concat(ArgumentList(target)).Select(Quote));

    public (string FileName, string Arguments) BuildCommandLine(string? target)
    {
        if (IsShell)
        {
            // The whole line has to reach /bin/sh as one argv entry. It is wrapped in double
            // quotes -- the single quotes ShellQuote adds around a target are then ordinary
            // characters to the shell, which is exactly what they are for.
            var line = Render(target);
            return OperatingSystem.IsWindows()
                ? ("cmd.exe", $"/c {line}")
                : ("/bin/sh", "-c \"" + line.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
        }

        // No shell: the documented form passes each argument as-is, so an argument containing
        // a space is quoted for the child process, not for a shell.
        return (SplitCommandLine().First(), string.Join(' ', ArgumentList(target).Select(Quote)));
    }

    private IEnumerable<string> ArgumentList(string? target)
        => (Args.Count == 0 ? SplitCommandLine().Skip(1) : Args)
            .Select(argument => argument.Replace("{target}", target ?? string.Empty, StringComparison.Ordinal))
            .Where(argument => argument.Length > 0);

    private IReadOnlyList<string> SplitCommandLine()
    {
        var tokens = Command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? [Command] : tokens;
    }

    public string? WorkingDirectoryFor(string root)
        => string.IsNullOrWhiteSpace(WorkingDirectory) ? root : Path.GetFullPath(Path.Combine(root, WorkingDirectory));

    /// <summary>
    /// Shell syntax DevX would have to interpret. Deliberately narrow: a path that merely
    /// contains a `-` or a `.` is not shell syntax, and treating it as such would silently
    /// route every ordinary command through a shell.
    /// </summary>
    public static bool LooksLikeShellCommandLine(string command)
        => command.Contains("&&", StringComparison.Ordinal)
            || command.Contains("||", StringComparison.Ordinal)
            || command.Contains('|')
            || command.Contains(';')
            || command.Contains('>')
            || command.Contains('<')
            || command.Contains('$')
            || Regex.IsMatch(command, @"(^|\s)cd\s");

    private static string Quote(string value)
        => value.Length == 0 || value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private static string ShellQuote(string? target)
        => string.IsNullOrWhiteSpace(target) ? string.Empty : "'" + target.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// DevX runs the repository's build and test commands. It never installs anything: an agent
/// that finds a missing dependency must be told to install it itself, not discover that
/// `devx test` quietly wrote to the environment it is measuring.
/// </summary>
public static class PackageInstallGuard
{
    private static readonly string[] Installers =
        ["pip", "pip3", "poetry", "uv", "conda", "npm", "yarn", "pnpm", "bun", "brew", "apt", "apt-get", "gem", "cargo", "composer", "dotnet"];

    private static readonly string[] InstallVerbs = ["install", "add", "update", "upgrade", "restore", "sync"];

    public static bool IsInstall(string commandLine)
    {
        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\''))
            .ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            var executable = Path.GetFileName(tokens[index]).ToLowerInvariant();
            if (executable.EndsWith(".exe", StringComparison.Ordinal)) executable = executable[..^4];
            if (executable.EndsWith(".cmd", StringComparison.Ordinal) || executable.EndsWith(".bat", StringComparison.Ordinal))
            {
                executable = executable[..^4];
            }

            // `python -m pip install ...` / `python3 -m pip install ...`
            if (tokens.Count > index + 2 && tokens[index + 1] == "-m"
                && Path.GetFileName(tokens[index + 2]).ToLowerInvariant() is "pip" or "pip3" or "ensurepip" or "venv")
            {
                if (tokens.Skip(index + 3).Any(token => InstallVerbs.Contains(token, StringComparer.OrdinalIgnoreCase))) return true;
            }

            if (!Installers.Contains(executable, StringComparer.Ordinal)) continue;

            // `dotnet` only installs with an explicit verb; `dotnet build` and `dotnet test` are fine.
            var verb = tokens.Skip(index + 1).FirstOrDefault(token => !token.StartsWith('-'));
            if (verb is not null && InstallVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                if (executable != "dotnet" || verb.Equals("install", StringComparison.OrdinalIgnoreCase) || verb.Equals("restore", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
