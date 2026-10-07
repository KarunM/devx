using DevX.Application;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class HealthAndDoctorIntegrationTests
{
    [Fact]
    public async Task EvaluateAsync_ReturnsHealthyForIndexedDotnetWorkspace()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-health-healthy").FullName;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.sln"), string.Empty);

        var service = new RuntimeHealthService();
        var health = await service.EvaluateAsync(repositoryRoot, repositoryIndexed: true, indexedFiles: 12);
        var doctor = service.BuildDoctor(health);

        Assert.Equal("Healthy", health.Status);
        Assert.Equal("dotnet", health.Context.RuntimeFamily);
        Assert.Empty(health.BlockingConditions);
        Assert.Empty(doctor.Findings);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsUnavailableWhenWorkspaceIsNotIndexedAndHasNoTarget()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-health-unavailable").FullName;

        var service = new RuntimeHealthService();
        var health = await service.EvaluateAsync(repositoryRoot, repositoryIndexed: false, indexedFiles: 0);
        var doctor = service.BuildDoctor(health);

        Assert.Equal("Unavailable", health.Status);
        Assert.Contains(health.BlockingConditions, condition => condition.Contains("not indexed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(health.BlockingConditions, condition => condition.Contains("No runtime target", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(doctor.Findings);
        Assert.Contains(doctor.Findings, finding => finding.Severity == "Error");
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsDegradedForIndexedWorkspaceWithoutGitRoot()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-health-degraded").FullName;
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var service = new RuntimeHealthService();
        var health = await service.EvaluateAsync(repositoryRoot, repositoryIndexed: true, indexedFiles: 3);
        var doctor = service.BuildDoctor(health);

        Assert.Equal("Degraded", health.Status);
        Assert.Contains(health.Warnings, warning => warning.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(doctor.Findings, finding => finding.Severity == "Warning");
    }

    [Fact]
    public async Task EvaluateAsync_TreatsGitWorktreePointerAsHealthy()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-health-worktree").FullName;
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".git"), "gitdir: /tmp/main/.git/worktrees/feature\n");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var health = await new RuntimeHealthService().EvaluateAsync(repositoryRoot, repositoryIndexed: true, indexedFiles: 3);

        Assert.Equal("Healthy", health.Checks.Single(check => check.Name == "git").Status);
        Assert.DoesNotContain(health.Warnings, warning => warning.Contains(".git", StringComparison.OrdinalIgnoreCase));
    }
}
