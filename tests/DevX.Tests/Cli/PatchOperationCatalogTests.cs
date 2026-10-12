using DevX.Cli;
using System.Text.Json;
using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Cli;

/// <summary>
/// OSS11 item 2. On 1.6.10 an agent invented patch ops ('replace', 'replace_text',
/// 'replace-text', 'update', 'edit') and only found out at `patch validate`, one round trip
/// later. `patch create` now refuses the op it cannot apply, where the agent typed it.
/// </summary>
public sealed class PatchOperationCatalogTests : CliHarness
{
    [Fact]
    public void Catalog_IsExactlyWhatApplyOperationAccepts()
    {
        // PatchLifecycleService.ApplyOperation's switch is Ordinal, so the catalog is too.
        Assert.Equal(["CreateFile", "DeleteFile", "ReplaceMethod", "ReplaceText",
            "InsertMethod", "AddUsing", "InsertText", "ReplaceLineRange"],
            PatchOperationCatalog.Names);
        Assert.True(PatchOperationCatalog.IsKnown("ReplaceText"));
        Assert.False(PatchOperationCatalog.IsKnown("replacetext"));
        Assert.False(PatchOperationCatalog.IsKnown(null));
        // TextReplace has worked since before 1.6.10 and keeps working; it is accepted but
        // not advertised as its own op, and this release adds no new aliases.
        Assert.True(PatchOperationCatalog.IsKnown("TextReplace"));
        Assert.DoesNotContain("TextReplace", PatchOperationCatalog.Names);
    }

    [Theory]
    [InlineData("replace_text", "ReplaceText")]
    [InlineData("replace-text", "ReplaceText")]
    [InlineData("replacetext", "ReplaceText")]
    [InlineData("ReplaceText ", "ReplaceText")]
    public void Catalog_SuggestsTheNearestRealOp(string invented, string expected)
    {
        Assert.Equal(expected, PatchOperationCatalog.Closest(invented));
    }

    [Fact]
    public void Catalog_SaysNothingWhenThereIsNoNearMiss()
    {
        // 'replace', 'update' and 'edit' are words, not misspellings, and 'replace' is
        // ambiguous between three real ops. They get no "did you mean" -- the message's
        // first line already sends the agent to `devx replace`, which is the actual fix.
        Assert.Null(PatchOperationCatalog.Closest("replace"));
        Assert.Null(PatchOperationCatalog.Closest("update"));
        Assert.Null(PatchOperationCatalog.Closest("edit"));
        Assert.Null(PatchOperationCatalog.Closest("frobnicate"));
        Assert.Null(PatchOperationCatalog.Closest(""));
    }

    [Fact]
    public void Catalog_HelpTextCarriesEveryOpAndFlag()
    {
        var help = PatchOperationCatalog.HelpText();
        foreach (var op in PatchOperationCatalog.Names)
        {
            Assert.Contains(op, help);
        }
        Assert.Contains("--old-text", help);
        Assert.Contains("--replacement", help);
        Assert.Contains("--position", help);
        Assert.Contains("devx replace <file> --old <text> --new <text>", help);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("replace_text")]
    [InlineData("replace-text")]
    [InlineData("update")]
    [InlineData("edit")]
    public async Task PatchCreate_RefusesAnInventedOpAndNamesReplace(string op)
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var artifact = Path.Combine(r, "p.json");
            var (code, output) = await RunAsync(r, "patch", "create", artifact, "--session",
                Guid0, "--op", op, "--path", "X.cs");
            Assert.Equal(1, code);
            Assert.Contains($"unknown op '{op}'; for a text edit use: "
                            + "devx replace <file> --old <text> --new <text>", output);
            Assert.Contains("Supported ops: CreateFile, DeleteFile, ReplaceMethod, "
                            + "ReplaceText, InsertMethod, AddUsing, InsertText, ReplaceLineRange.",
                output);
            // Refused BEFORE writing: no half-made artifact for the agent to validate.
            Assert.False(File.Exists(artifact), "an unknown op must not produce a patch file");
        });
    }

    [Theory]
    [InlineData("CreateFile")]
    [InlineData("ReplaceText")]
    [InlineData("TextReplace")]
    [InlineData("ReplaceMethod")]
    [InlineData("InsertMethod")]
    [InlineData("AddUsing")]
    [InlineData("InsertText")]
    [InlineData("ReplaceLineRange")]
    [InlineData("DeleteFile")]
    public async Task PatchCreate_StillAcceptsEveryRealOp(string op)
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var artifact = Path.Combine(r, "ok.json");
            var (code, output) = await RunAsync(r, "patch", "create", artifact, "--session",
                Guid0, "--op", op, "--path", "X.cs", "--content", "x");
            Assert.Equal(0, code);
            Assert.Contains("Patch artifact written", output);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
            Assert.Equal(op, document.RootElement.GetProperty("operations")[0]
                .GetProperty("operation").GetString());
        });
    }

    [Fact]
    public async Task PatchCreate_HelpListsTheOpsAndTheirFlags()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "patch", "create", "--help");
            Assert.Equal(0, code);
            foreach (var op in PatchOperationCatalog.Names)
            {
                Assert.Contains(op, output);
            }
            Assert.Contains("--old-text", output);
            Assert.Contains("--position", output);
            Assert.Contains("devx replace <file> --old <text> --new <text>", output);
            Assert.DoesNotContain("Unknown command", output);
        });
    }

    [Fact]
    public async Task PatchCreate_MissingOpStillNamesTheSupportedOnes()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "patch", "create",
                Path.Combine(r, "p.json"), "--session", Guid0, "--path", "X.cs");
            Assert.Equal(1, code);
            Assert.Contains("Missing --op. Supported: " + string.Join(", ",
                PatchOperationCatalog.Names) + ".", output);
            Assert.Contains("devx replace <file> --old <text> --new <text>", output);
        });
    }
}
