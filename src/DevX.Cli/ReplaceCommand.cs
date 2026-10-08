using DevX.Protocol;

namespace DevX.Cli;

public static class ReplaceCommand
{
    public static int Run(string[] args, string root)
    {
        string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Length < 2 || Option("--old") is null || Option("--new") is null)
        { Console.Error.WriteLine("Usage: devx replace <file> --old <text> --new <text> [--all | --occurrence <n>]"); return 1; }
        int? occurrence = int.TryParse(Option("--occurrence"), out var n) ? n : null;
        var result = new TextReplaceService().Apply(root, args[1], Option("--old")!, Option("--new")!, args.Contains("--all"), occurrence);
        Console.WriteLine(result.Message);
        if (result.Diff.Length > 0) Console.WriteLine(result.Diff);
        return result.Success ? 0 : 1;
    }
}
