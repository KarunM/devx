using System.Text;

namespace DevX.Protocol;

public sealed record TextReplaceResult(bool Success, int Matches, int Replaced, string Message, string Diff);

public sealed class TextReplaceService
{
    public TextReplaceResult Apply(string repositoryRoot, string relativePath, string oldText, string replacement, bool replaceAll = false, int? occurrence = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != root)
            return new(false, 0, 0, $"replace failed: path '{relativePath}' is outside the repository root", "");
        if (!File.Exists(path)) return new(false, 0, 0, $"replace failed: file not found: {relativePath}", "");
        var before = File.ReadAllText(path);
        var starts = new List<int>();
        for (var i = 0; (i = before.IndexOf(oldText, i, StringComparison.Ordinal)) >= 0; i += oldText.Length) starts.Add(i);
        if (starts.Count == 0) return new(false, 0, 0, $"replace failed: old text not found in {relativePath}", "");
        if (occurrence is < 1 || occurrence > starts.Count) return new(false, starts.Count, 0, $"replace failed: occurrence {occurrence} is out of range in {relativePath}", "");
        if (starts.Count > 1 && !replaceAll && occurrence is null)
        {
            var lines = starts.Select(i => before[..i].Count(c => c == '\n') + 1);
            return new(false, starts.Count, 0, $"replace failed: old text matches {starts.Count} times in {relativePath} (lines {string.Join(", ", lines)}); use --all or --occurrence n", "");
        }
        var chosen = occurrence is int n ? new[] { starts[n - 1] } : replaceAll ? starts.ToArray() : new[] { starts[0] };
        var after = before;
        foreach (var index in chosen.Reverse()) after = after.Remove(index, oldText.Length).Insert(index, replacement);
        var temp = path + ".devx-tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, after, new UTF8Encoding(false)); File.Move(temp, path, true); }
        catch { if (File.Exists(temp)) File.Delete(temp); return new(false, starts.Count, 0, $"replace failed: could not write {relativePath}", ""); }
        var diff = $"--- {relativePath}\n+++ {relativePath}\n@@\n-{oldText}\n+{replacement}";
        return new(true, starts.Count, chosen.Length, $"replaced {chosen.Length} occurrence(s) in {relativePath}", diff);
    }
}
