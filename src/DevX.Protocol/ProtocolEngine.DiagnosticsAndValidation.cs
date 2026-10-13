using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed partial class ProtocolEngine
{
    /// <summary>
    /// A bare "Build failed." / "Tests failed." is what sent the LG1 agent to grep: the run had
    /// already parsed the file, the line and the message, and then threw them away in the error.
    /// The first few are rendered in the same `path:line: message` form the CLI summary uses,
    /// and the full list stays in the log path the execution record carries.
    /// </summary>
    private static string CompactFailure(string verb, ExecutionRecord execution, string root)
    {
        var lines = execution.Diagnostics
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Message))
            .Take(5)
            .Select(diagnostic => CompactDiagnostic(diagnostic, root))
            .ToList();
        if (lines.Count == 0)
        {
            return string.IsNullOrWhiteSpace(execution.Summary) ? $"{verb} failed." : execution.Summary;
        }
        return $"{verb} failed:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string CompactDiagnostic(DiagnosticRecord diagnostic, string root)
    {
        var file = diagnostic.File;
        if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file)) file = Path.GetRelativePath(root, file);
        var message = diagnostic.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(file)
            ? $"{diagnostic.Code}: {message}"
            : $"{file}:{diagnostic.Line ?? 0}: {diagnostic.Code} {message}";
    }

    private async Task<ProtocolResponse> HandleRunBuildAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Build);
        var configured = DevXCommandConfig.TryRead(root, "build") is not null;
        if ((!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null) && !configured)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var (buildContext, buildTarget) = ResolvedOrConfigured(root, resolution, "build");
        var execution = await _buildTestService.RunBuildAsync(root, resolution.TargetSelector, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var response = new ValidationExecutionResponse("build", buildContext, buildTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Build", execution, root))]);
    }

    private async Task<ProtocolResponse> HandleRunTestsAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Test);
        var configured = DevXCommandConfig.TryRead(root, "test") is not null;
        if ((!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null) && !configured)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var targets = ReadStringArray(request.Payload, "targets");
        // A target the caller did not name is filled in from runtime discovery -- but only when
        // the repository has not answered for itself. A configured test command takes `{target}`
        // as the caller wrote it, and a bare `devx test` must leave it empty: substituting the
        // discovered project path would run `runtests.py .` instead of `runtests.py`.
        if (targets.Count == 0 && !configured && !string.IsNullOrWhiteSpace(resolution.TargetSelector))
        {
            targets = [resolution.TargetSelector];
        }
        var timeoutSeconds = ReadPositiveInt(request.Payload, "timeoutSeconds") ?? 300;
        var testFilter = request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind == JsonValueKind.String ? filterElement.GetString() : null;
        var execution = await _buildTestService.RunTestsAsync(root, targets, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, testFilter);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var (testContext, testTarget) = ResolvedOrConfigured(root, resolution, "test");
        var response = new ValidationExecutionResponse("test", testContext, testTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Tests", execution, root))]);
    }

    /// <summary>`devx verify [build|test|both] [target...]` in one call: the build, then the
    /// relevant tests unless the build failed or the mode was build-only. Both runs come back in
    /// one response, so an agent that edits and checks spends one call instead of three.
    /// A build failure skips the tests and is reported, never hidden.</summary>
    private async Task<ProtocolResponse> HandleRunVerifyAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var mode = VerifyAfterEdit.ParseMode(ReadString(request.Payload, "mode"));
        var configuredBuild = DevXCommandConfig.TryRead(root, "build") is not null;
        var configuredTests = DevXCommandConfig.TryRead(root, "test") is not null;

        var buildResolution = ResolveValidationContext(root, request.Payload, ValidationMode.Build);
        if ((!buildResolution.Success || buildResolution.Context is null || buildResolution.SelectedTarget is null) && !configuredBuild)
        {
            return Fail("ValidationError", buildResolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var (buildContext, buildTarget) = ResolvedOrConfigured(root, buildResolution, "build");
        // `--verify=test` is the Python case: there is nothing to compile, so a build record that
        // says "skipped" is more honest than a py_compile pass dressed up as a build.
        var buildExecution = mode == VerifyMode.Test
            ? VerifyAfterEdit.TestOnlyBuild(ReadString(request.Payload, "target") ?? string.Empty)
            : await _buildTestService.RunBuildAsync(root, buildResolution.TargetSelector, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, buildExecution), cancellationToken);
        foreach (var diagnostic in buildExecution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }
        var buildResponse = new ValidationExecutionResponse("build", buildContext, buildTarget, buildExecution);

        if (!buildExecution.Success || mode == VerifyMode.Build)
        {
            var testsSkipped = !buildExecution.Success;
            var only = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, null, testsSkipped);
            return new ProtocolResponse(buildExecution.Success, JsonSerializer.SerializeToElement(only), [],
                buildExecution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Build", buildExecution, root))]);
        }

        var testResolution = ResolveValidationContext(root, request.Payload, ValidationMode.Test);
        if ((!testResolution.Success || testResolution.Context is null || testResolution.SelectedTarget is null) && !configuredTests)
        {
            var unresolved = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, null, true);
            return new ProtocolResponse(false, JsonSerializer.SerializeToElement(unresolved), [],
                [new ProtocolError("ValidationError", testResolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.")]);
        }

        var targets = ReadStringArray(request.Payload, "targets");
        if (targets.Count == 0 && !configuredTests && !string.IsNullOrWhiteSpace(testResolution.TargetSelector))
        {
            targets = [testResolution.TargetSelector];
        }
        else if (targets.Count == 1 && VerifyAfterEdit.HasBuildStep(targets[0]) && !VerifyAfterEdit.IsTestFile(root, Path.Combine(root, targets[0])))
        {
            // A named source file means "the tests for this file", not "run this file as a test" --
            // the same rule `devx replace --verify` uses, so the two never disagree.
            var relevant = VerifyAfterEdit.FindRelevantTestFiles(root, targets[0]);
            if (relevant.Count > 0) targets = relevant;
        }

        var timeoutSeconds = ReadPositiveInt(request.Payload, "timeoutSeconds") ?? 300;
        var testFilter = ReadString(request.Payload, "filter");
        var testExecution = await _buildTestService.RunTestsAsync(root, targets, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, testFilter);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, testExecution), cancellationToken);
        foreach (var diagnostic in testExecution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var (testContext, testTarget) = ResolvedOrConfigured(root, testResolution, "test");
        var testResponse = new ValidationExecutionResponse("test", testContext, testTarget, testExecution);
        var success = testExecution.Success;
        var result = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, testResponse, false);
        return new ProtocolResponse(success, JsonSerializer.SerializeToElement(result), [],
            success ? [] : [new ProtocolError("ValidationError", CompactFailure("Tests", testExecution, root))]);
    }

    private async Task<ProtocolResponse> HandleGetDiagnosticsAsync(SessionState session, CancellationToken cancellationToken)
    {
        var current = await _sessionStore.GetSessionAsync(new GetSessionRequest(session.Id), cancellationToken);
        var diagnostics = new List<DiagnosticRecord>();
        if (current is not null)
        {
            foreach (var entry in current.DiagnosticsHistory)
            {
                var diagnostic = JsonSerializer.Deserialize<DiagnosticRecord>(entry);
                if (diagnostic is not null)
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new DiagnosticsResponse(diagnostics)), [], []);
    }
}
