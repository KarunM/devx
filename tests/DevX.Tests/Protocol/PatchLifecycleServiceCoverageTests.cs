using DevX.Domain;
using DevX.Protocol;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class PatchLifecycleServiceCoverageTests
{
    [Fact]
    public void ValidateDocument_ReportsExistingCreateTargetAsFailure()
    {
        var root = Directory.CreateTempSubdirectory("devx-patch-validation-failure").FullName;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, "Existing.txt"), "already here");
            var patch = new PatchDocument("1.0", [Operation("CreateFile", "Existing.txt", content: "replacement")]);

            var result = new PatchLifecycleService().ValidateDocument(root, patch);

            Assert.False(result.Valid);
            Assert.Contains(result.Errors, error => error.Contains("already exists", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Rollback_RestoresBeforeStateForReplaceMethod()
    {
        var root = Directory.CreateTempSubdirectory("devx-patch-rollback").FullName;
        const string original = "namespace Sample; public sealed class SampleType { public string Value() => \"before\"; }";
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, "Sample.cs"), original);
            var patch = new PatchDocument("1.0", [new PatchOperation("ReplaceMethod", "Sample.cs", null, "Value", "public string Value() => \"after\";", null, null, null, null, null, original)]);

            var (response, error) = new PatchLifecycleService().Rollback(root, patch);

            Assert.Null(error);
            Assert.NotNull(response);
            Assert.Equal(1, response!.FilesRestored);
            Assert.Equal(original, System.IO.File.ReadAllText(Path.Combine(root, "Sample.cs")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Preview_DescribesCreateFileChange()
    {
        var root = Directory.CreateTempSubdirectory("devx-patch-preview").FullName;
        try
        {
            var patch = new PatchDocument("1.0", [Operation("CreateFile", "NewFile.txt", content: "created")]);

            var preview = new PatchLifecycleService().Preview(root, patch);

            Assert.True(preview.PatchValid);
            Assert.Contains("NewFile.txt", preview.UnifiedDiffPreview);
            Assert.Contains("created", preview.UnifiedDiffPreview);
            Assert.False(System.IO.File.Exists(Path.Combine(root, "NewFile.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReplaceText_AppliesUniqueMatch()
    {
        WithText("before middle after", (root, original) =>
        {
            var patch = new PatchDocument("1.0", [TextReplace("middle", "changed", original)]);
            var result = new PatchLifecycleService().Apply(root, patch);
            Assert.Null(result.Error);
            Assert.Equal("before changed after", System.IO.File.ReadAllText(Path.Combine(root, "Sample.txt")));
        });
    }

    [Fact]
    public void ReplaceText_RejectsAmbiguousAndMissingTextWithMatchCount()
    {
        WithText("same same", (root, original) =>
        {
            var service = new PatchLifecycleService();
            var ambiguous = service.ValidateDocument(root, new PatchDocument("1.0", [TextReplace("same", "new", original)]));
            Assert.Contains(ambiguous.Errors, error => error.Contains("2 matches", StringComparison.Ordinal));
            var missing = service.ValidateDocument(root, new PatchDocument("1.0", [TextReplace("absent", "new", original)]));
            Assert.Contains(missing.Errors, error => error.Contains("0 matches", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ReplaceText_SupportsOccurrenceAndReplaceAll()
    {
        WithText("x x x", (root, original) =>
        {
            var service = new PatchLifecycleService();
            var occurrence = TextReplace("x", "y", original) with { Occurrence = 2 };
            Assert.Null(service.Apply(root, new PatchDocument("1.0", [occurrence])).Error);
            Assert.Equal("x y x", System.IO.File.ReadAllText(Path.Combine(root, "Sample.txt")));
            System.IO.File.WriteAllText(Path.Combine(root, "Sample.txt"), original);
            var all = TextReplace("x", "z", original) with { ReplaceAll = true };
            Assert.Null(service.Apply(root, new PatchDocument("1.0", [all])).Error);
            Assert.Equal("z z z", System.IO.File.ReadAllText(Path.Combine(root, "Sample.txt")));
        });
    }

    [Fact]
    public void ReplaceText_RollbackRestoresCapturedBeforeState()
    {
        WithText("old text", (root, original) =>
        {
            var service = new PatchLifecycleService();
            var patch = new PatchDocument("1.0", [TextReplace("old", "new", original)]);
            Assert.Null(service.Apply(root, patch).Error);
            var (rollback, error) = service.Rollback(root, patch);
            Assert.Null(error);
            Assert.True(rollback!.Success);
            Assert.Equal(original, System.IO.File.ReadAllText(Path.Combine(root, "Sample.txt")));
        });
    }

    private static void WithText(string contents, Action<string, string> action)
    {
        var root = Directory.CreateTempSubdirectory("devx-text-replace").FullName;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, "Sample.txt"), contents);
            action(root, contents);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static PatchOperation TextReplace(string oldText, string replacement, string beforeState) =>
        new("ReplaceText", "Sample.txt", null, null, replacement, null, null, null, null, null, beforeState, oldText);

    private static PatchOperation Operation(string operation, string path, string? content = null) =>
        new(operation, path, content, null, null, null, null, null, null, null, null);
}
