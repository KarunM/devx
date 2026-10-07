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

    private static PatchOperation Operation(string operation, string path, string? content = null) =>
        new(operation, path, content, null, null, null, null, null, null, null, null);
}
