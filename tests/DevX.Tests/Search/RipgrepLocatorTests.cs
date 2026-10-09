using DevX.Infrastructure.Storage.SQLite;
using Xunit;

namespace DevX.Tests.Search;

/// <summary>Brief OSS13 item 5: ripgrep ships with DevX. The lookup order is
/// `.devx/config.json` rg.path -> bundled copy -> `rg` on PATH -> nothing, and `devx doctor`
/// reports which one answered. These pin the order and the descriptions.</summary>
public sealed class RipgrepLocatorTests
{
    /// <summary>A repository whose `.devx/config.json` names its own rg.</summary>
    private static string RepositoryWithConfiguredRg(string rgPath)
    {
        var root = Directory.CreateTempSubdirectory("devx-rg-config").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        File.WriteAllText(Path.Combine(root, ".devx", "config.json"),
            "{\n  \"rg\": { \"path\": \"" + rgPath.Replace("\\", "\\\\") + "\" }\n}\n");
        return root;
    }

    private static string WriteFakeRg(string directory, string? name = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name ?? RipgrepLocator.FileName());
        File.WriteAllText(path, "not really ripgrep\n");
        return path;
    }

    [Fact]
    public void ConfiguredRgWinsOverBundled()
    {
        var configured = WriteFakeRg(Directory.CreateTempSubdirectory("devx-rg-cfg").FullName);
        var root = RepositoryWithConfiguredRg(configured);
        var bundledDirectory = Directory.CreateTempSubdirectory("devx-rg-bundled").FullName;
        WriteFakeRg(bundledDirectory);

        var location = RipgrepLocator.Locate(root, bundledDirectory);

        Assert.Equal(RipgrepEngine.Configured, location.Engine);
        Assert.Equal(configured, location.Executable);
    }

    [Fact]
    public void BundledRgWinsOverPath()
    {
        // No config, so the bundled copy is reached before PATH is ever consulted. The
        // assertion holds whether or not this machine has rg installed.
        var bundledDirectory = Directory.CreateTempSubdirectory("devx-rg-only-bundled").FullName;
        var bundled = WriteFakeRg(bundledDirectory);

        var location = RipgrepLocator.Locate(null, bundledDirectory);

        Assert.Equal(RipgrepEngine.Bundled, location.Engine);
        Assert.Equal(bundled, location.Executable);
        Assert.Contains("(bundled)", location.Describe());
    }

    [Fact]
    public void BundledRgIsFoundInTheToolPackageLayout()
    {
        // The tool package unpacks to tools/<tfm>/any/rg-tools/<rid>/rg, so the locator has
        // to look there and not only beside the binary.
        var baseDirectory = Directory.CreateTempSubdirectory("devx-rg-toolpack").FullName;
        var bundled = WriteFakeRg(Path.Combine(baseDirectory, "rg-tools", RipgrepLocator.RuntimeIdentifier()));

        var location = RipgrepLocator.Locate(null, baseDirectory);

        Assert.Equal(RipgrepEngine.Bundled, location.Engine);
        Assert.Equal(bundled, location.Executable);
    }

    [Fact]
    public void ConfiguredPathThatDoesNotExistIsIgnored()
    {
        var root = RepositoryWithConfiguredRg(Path.Combine("tools", "missing-rg"));
        Assert.Null(RipgrepLocator.ConfiguredPath(root));

        var location = RipgrepLocator.Locate(root, Directory.CreateTempSubdirectory("devx-rg-empty").FullName);
        Assert.NotEqual(RipgrepEngine.Configured, location.Engine);
    }

    [Fact]
    public void ConfiguredPathIsRelativeToTheRepositoryRoot()
    {
        var root = Directory.CreateTempSubdirectory("devx-rg-relative").FullName;
        var executable = WriteFakeRg(Path.Combine(root, "tools"));
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        File.WriteAllText(Path.Combine(root, ".devx", "config.json"), "{ \"rg\": { \"path\": \"tools/rg\" } }\n");

        Assert.Equal(executable, RipgrepLocator.ConfiguredPath(root));
    }

    [Fact]
    public void MissingConfigIsNotAnError()
    {
        var root = Directory.CreateTempSubdirectory("devx-rg-noconfig").FullName;
        Assert.Null(RipgrepLocator.ConfiguredPath(root));
        Assert.Null(RipgrepLocator.ConfiguredPath(null));
    }

    [Fact]
    public void MalformedConfigIsIgnoredRatherThanFatal()
    {
        var root = Directory.CreateTempSubdirectory("devx-rg-badconfig").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        File.WriteAllText(Path.Combine(root, ".devx", "config.json"), "{ this is not json");
        Assert.Null(RipgrepLocator.ConfiguredPath(root));
    }

    [Fact]
    public void WithNoConfigNoBundleAndNoRgOnPathTheScanAnswers()
    {
        // Like RipgrepContentSearcherTests, this cannot force PATH to be empty without
        // disturbing tests running in parallel, so it skips where rg is installed.
        if (RipgrepLocator.PathHit() is not null) return;
        var empty = Directory.CreateTempSubdirectory("devx-rg-none").FullName;

        var location = RipgrepLocator.Locate(null, empty);

        Assert.Equal(RipgrepEngine.None, location.Engine);
        Assert.Null(location.Executable);
        Assert.Equal("built-in regex scan (no ripgrep installed)", location.Describe());
    }

    [Fact]
    public void DescribeNamesConfiguredAndPathEngines()
    {
        var rg = WriteFakeRg(Directory.CreateTempSubdirectory("devx-rg-describe").FullName);
        Assert.Contains("(configured:", new RipgrepLocation(rg, RipgrepEngine.Configured, "9.9.9").Describe());
        Assert.Contains("(on PATH)", new RipgrepLocation(rg, RipgrepEngine.Path, "9.9.9").Describe());
        Assert.Contains("ripgrep 9.9.9", new RipgrepLocation(rg, RipgrepEngine.Bundled, "9.9.9").Describe());
    }
}
