using System.Reflection;
using System.Text.Json;
using DevX.Domain;
using DevX.Protocol;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class PatchLifecycleAtomicityTests
{
    [Fact]
    public void Apply_StagesOperationsInOrderAndLeavesLiveFileUntouchedWhenLaterOperationFails()
    {
        WithRoot(root =>
        {
            const string original = "A B";
            var target = Path.Combine(root, "Sample.txt");
            IOFile.WriteAllText(target, original);
            var patch = new PatchDocument("1.0", [
                TextReplace("Sample.txt", "A B", "Z"),
                TextReplace("Sample.txt", "B", "Y")]);

            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Response);
            Assert.Equal("ValidationError", result.Error!.Code);
            Assert.Equal(original, IOFile.ReadAllText(target));
        });
    }

    [Fact]
    public void Apply_MapsAbsoluteInRootTargetsIntoStagingTree()
    {
        WithRoot(root =>
        {
            const string original = "before";
            var target = Path.Combine(root, "Sample.txt");
            IOFile.WriteAllText(target, original);
            var patch = new PatchDocument("1.0", [TextReplace(target, "before", "after")]);

            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Error);
            Assert.True(result.Response!.Applied);
            Assert.Equal(target, Assert.Single(result.Response.Summary.FilesAffected));
            Assert.Equal("after", IOFile.ReadAllText(target));
        });
    }

    [Fact]
    public void Apply_RejectsOutsideAndSiblingPrefixPathsBeforeReadingOrWritingThem()
    {
        var parent = Directory.CreateTempSubdirectory("devx-patch-boundary-");
        var root = Path.Combine(parent.FullName, "repo");
        var sibling = Path.Combine(parent.FullName, "repo-sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        try
        {
            const string outsideContents = "outside";
            var outsideFile = Path.Combine(sibling, "Sample.txt");
            IOFile.WriteAllText(outsideFile, outsideContents);
            var patch = new PatchDocument("1.0", [TextReplace(outsideFile, "outside", "changed")]);

            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Response);
            Assert.Equal("ValidationError", result.Error!.Code);
            Assert.Equal(outsideContents, IOFile.ReadAllText(outsideFile));
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public void Apply_RejectsSymlinkComponentsBeforeStagingExternalTarget()
    {
        var parent = Directory.CreateTempSubdirectory("devx-patch-link-");
        var root = Path.Combine(parent.FullName, "repo");
        var outside = Path.Combine(parent.FullName, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "Sample.txt");
        IOFile.WriteAllText(outsideFile, "outside");
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // The platform or test account does not permit creating symlinks.
            }

            var patch = new PatchDocument("1.0", [TextReplace("linked/Sample.txt", "outside", "changed")]);
            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Response);
            Assert.Equal("ValidationError", result.Error!.Code);
            Assert.Equal("outside", IOFile.ReadAllText(outsideFile));
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public void Apply_CommitsMultipleFilesAfterAllOperationsAreStaged()
    {
        WithRoot(root =>
        {
            IOFile.WriteAllText(Path.Combine(root, "A.txt"), "first");
            IOFile.WriteAllText(Path.Combine(root, "B.txt"), "second");
            var patch = new PatchDocument("1.0", [
                TextReplace("A.txt", "first", "updated-first"),
                TextReplace("B.txt", "second", "updated-second")]);

            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Error);
            Assert.True(result.Response!.Applied);
            Assert.Equal("updated-first", IOFile.ReadAllText(Path.Combine(root, "A.txt")));
            Assert.Equal("updated-second", IOFile.ReadAllText(Path.Combine(root, "B.txt")));
        });
    }

    [Fact]
    public void Apply_PreservesExistingUnixFileMode()
    {
        WithRoot(root =>
        {
            if (OperatingSystem.IsWindows()) return;
            var target = Path.Combine(root, "script.sh");
            IOFile.WriteAllText(target, "before");
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            IOFile.SetUnixFileMode(target, mode);

            var result = new PatchLifecycleService().Apply(root,
                new PatchDocument("1.0", [TextReplace("script.sh", "before", "after")]));

            Assert.Null(result.Error);
            Assert.Equal(mode, IOFile.GetUnixFileMode(target));
        });
    }

    [Fact]
    public void Apply_RestoresByteExactFilesAfterCommitFailure()
    {
        WithRoot(root =>
        {
            var originalA = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A };
            var originalB = new byte[] { (byte)'b', 0x00, 0xFF };
            IOFile.WriteAllBytes(Path.Combine(root, "A.txt"), originalA);
            IOFile.WriteAllBytes(Path.Combine(root, "B.txt"), originalB);
            var patch = new PatchDocument("1.0", [
                TextReplace("A.txt", "a", "changed-a"),
                TextReplace("B.txt", "b", "changed-b")]);
            var service = ServiceWithFaultHook((phase, index) =>
            {
                if (phase == "commit" && index == 1) throw new IOException("controlled commit failure");
            });

            var result = service.Apply(root, patch);

            Assert.Null(result.Response);
            Assert.Equal("PatchCommitFailedRestored", result.Error!.Code);
            Assert.Equal(originalA, IOFile.ReadAllBytes(Path.Combine(root, "A.txt")));
            Assert.Equal(originalB, IOFile.ReadAllBytes(Path.Combine(root, "B.txt")));
        });
    }

    [Fact]
    public void Apply_ReportsUncertainOutcomeWhenRollbackFails()
    {
        WithRoot(root =>
        {
            var originalA = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'f', (byte)'i', (byte)'r', (byte)'s', (byte)'t', 0x0D, 0x0A };
            var originalB = System.Text.Encoding.UTF8.GetBytes("second");
            IOFile.WriteAllBytes(Path.Combine(root, "A.txt"), originalA);
            IOFile.WriteAllBytes(Path.Combine(root, "B.txt"), originalB);
            var patch = new PatchDocument("1.0", [
                TextReplace("A.txt", "first", "updated-first"),
                TextReplace("B.txt", "second", "updated-second")]);
            var service = ServiceWithFaultHook((phase, index) =>
            {
                if (phase == "commit" && index == 1) throw new IOException("controlled commit failure");
                if (phase == "restore" && index == 0) throw new IOException("controlled restore failure");
            });

            var result = service.Apply(root, patch);

            Assert.Null(result.Response);
            Assert.Equal("PatchCommitOutcomeUncertain", result.Error!.Code);
            Assert.Equal("updated-first\r\n", IOFile.ReadAllText(Path.Combine(root, "A.txt")));
            Assert.Equal("second", IOFile.ReadAllText(Path.Combine(root, "B.txt")));

            var recoveryRoot = RecoveryLocation(result.Error.Message);
            try
            {
                Assert.True(Directory.Exists(recoveryRoot));
                using var manifest = JsonDocument.Parse(IOFile.ReadAllText(Path.Combine(recoveryRoot, "manifest.json")));
                Assert.Equal("uncertain-commit", manifest.RootElement.GetProperty("outcome").GetString());
                Assert.Contains("A.txt", manifest.RootElement.GetProperty("attemptedPaths").EnumerateArray().Select(value => value.GetString()));
                Assert.Contains("restore failure", manifest.RootElement.GetProperty("failures")[0].GetString());
                var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
                var backupA = files.Single(entry => entry.GetProperty("path").GetString() == "A.txt").GetProperty("backup").GetString()!;
                var backupB = files.Single(entry => entry.GetProperty("path").GetString() == "B.txt").GetProperty("backup").GetString()!;
                Assert.Equal(originalA, IOFile.ReadAllBytes(Path.Combine(recoveryRoot, backupA)));
                Assert.Equal(originalB, IOFile.ReadAllBytes(Path.Combine(recoveryRoot, backupB)));
            }
            finally { Directory.Delete(recoveryRoot, recursive: true); }
        });
    }

    [Fact]
    public void Apply_StagesCreateAndDeleteOperationsTogether()
    {
        WithRoot(root =>
        {
            IOFile.WriteAllText(Path.Combine(root, "Delete.txt"), "remove");
            var patch = new PatchDocument("1.0", [
                new PatchOperation("CreateFile", "nested/New.txt", "created", null, null, null, null, null, null, null, null),
                new PatchOperation("DeleteFile", "Delete.txt", null, null, null, null, null, null, null, null, null)]);

            var result = new PatchLifecycleService().Apply(root, patch);

            Assert.Null(result.Error);
            Assert.Equal("created", IOFile.ReadAllText(Path.Combine(root, "nested", "New.txt")));
            Assert.False(IOFile.Exists(Path.Combine(root, "Delete.txt")));
        });
    }

    [Fact]
    public void Rollback_ReportsUncertainAfterAnEarlierOperationWasApplied()
    {
        WithRoot(root =>
        {
            const string before = "before B";
            IOFile.WriteAllText(Path.Combine(root, "B.txt"), "after B");
            Directory.CreateDirectory(Path.Combine(root, "A.txt"));
            var patch = new PatchDocument("1.0", [
                TextReplace("A.txt", "before A", "after A") with { BeforeState = "before A" },
                TextReplace("B.txt", "before B", "after B") with { BeforeState = before }]);

            var (response, error) = new PatchLifecycleService().Rollback(root, patch);

            Assert.Null(response);
            Assert.Equal("PatchRollbackOutcomeUncertain", error!.Code);
            Assert.Equal(before, IOFile.ReadAllText(Path.Combine(root, "B.txt")));

            var recoveryRoot = RecoveryLocation(error.Message);
            try
            {
                using var manifest = JsonDocument.Parse(IOFile.ReadAllText(Path.Combine(recoveryRoot, "manifest.json")));
                Assert.Equal("uncertain-rollback", manifest.RootElement.GetProperty("outcome").GetString());
                var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
                var bEntry = files
                    .Single(entry => entry.GetProperty("path").GetString() == "B.txt");
                Assert.Equal(before, bEntry.GetProperty("requestedBeforeState").GetString());
                var backupB = bEntry.GetProperty("backup").GetString()!;
                Assert.Equal("after B", IOFile.ReadAllText(Path.Combine(recoveryRoot, backupB)));
                Assert.Contains("A.txt", manifest.RootElement.GetProperty("attemptedPaths").EnumerateArray().Select(value => value.GetString()));
            }
            finally { Directory.Delete(recoveryRoot, recursive: true); }
        });
    }

    private static PatchLifecycleService ServiceWithFaultHook(Action<string, int> hook)
    {
        var constructor = typeof(PatchLifecycleService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Action<string, int>)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (PatchLifecycleService)constructor!.Invoke([hook]);
    }

    private static string RecoveryLocation(string message)
    {
        const string marker = "Recovery data is at '";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected recovery location in error: {message}");
        start += marker.Length;
        var end = message.IndexOf('\'', start);
        Assert.True(end > start, $"Expected quoted recovery location in error: {message}");
        return message[start..end];
    }

    private static PatchOperation TextReplace(string path, string oldText, string replacement) =>
        new("ReplaceText", path, null, null, replacement, null, null, null, null, null, null, oldText);

    private static void WithRoot(Action<string> action)
    {
        var root = Directory.CreateTempSubdirectory("devx-patch-atomicity-");
        try { action(root.FullName); }
        finally { Directory.Delete(root.FullName, recursive: true); }
    }
}
