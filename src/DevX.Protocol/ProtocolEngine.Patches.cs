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
    private async Task<ProtocolResponse> HandlePatchAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var patch = JsonSerializer.Deserialize<PatchDocument>(request.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (patch is null || patch.Version != "1.0")
        {
            return Fail("ValidationError", "Invalid patch document.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (_repositoryStore is not IIndexFreshnessStore freshnessStore)
        {
            return Fail("IndexFreshnessUnavailable", "The repository store cannot durably track source/index freshness; patch was not applied.");
        }
        var affectedFiles = patch.Operations.Select(operation => NormalizePatchDirtyPath(root, operation.Path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (!await freshnessStore.TryMarkIndexFilesDirtyAsync(root, affectedFiles, cancellationToken))
        {
            return Fail("IndexDirty", "Patch was not applied because this repository already has unreconciled source changes. Run 'devx index <root>' first.");
        }

        var (result, error) = _patchLifecycleService.Apply(root, patch);
        if (error is not null)
        {
            if (error.Code is "PatchCommitOutcomeUncertain" or "PatchRollbackOutcomeUncertain")
            {
                await freshnessStore.MarkIndexRepositoryDirtyAsync(root, affectedFiles, cancellationToken);
            }
            else
            {
                await freshnessStore.ClearDirtyIndexFilesAsync(root, affectedFiles, cancellationToken);
            }
            return Fail(error.Code, error.Message);
        }

        var appliedResult = result!;
        var refreshFailure = _refreshIndexAfterEdit ? await RefreshIndexAsync(root, affectedFiles, cancellationToken) : null;
        if (refreshFailure is not null)
        {
            var pending = await freshnessStore.GetDirtyIndexRepositoryAsync(root, cancellationToken);
            var files = pending is null ? "unknown files" : string.Join(", ", pending.DirtyFiles);
            return Fail("IndexRefreshFailedDirty", $"Patch source changes are applied, but indexed operations are blocked until reconciliation succeeds for {files}: {refreshFailure}. Run 'devx index <root>'.");
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(appliedResult), appliedResult.ValidationErrors, []);
    }

    private async Task<ProtocolResponse> HandlePatchPreviewAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var patch = JsonSerializer.Deserialize<PatchDocument>(request.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (patch is null || patch.Version != "1.0")
        {
            return Fail("ValidationError", "Invalid patch document.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var response = _patchLifecycleService.Preview(root, patch);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandlePatchValidateAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var patch = JsonSerializer.Deserialize<PatchDocument>(request.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (patch is null || patch.Version != "1.0")
        {
            return Fail("ValidationError", "Invalid patch document.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var response = _patchLifecycleService.ValidateDocument(root, patch);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private static string NormalizePatchDirtyPath(string root, string path)
    {
        if (RepositoryPathGuard.TryResolve(root, path, out var absolutePath, out _))
            return Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        return path.Replace('\\', '/');
    }

    private async Task<ProtocolResponse> HandlePatchRollbackAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var patch = JsonSerializer.Deserialize<PatchDocument>(request.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (patch is null || patch.Version != "1.0")
        {
            return Fail("ValidationError", "Invalid patch document.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (_repositoryStore is not IIndexFreshnessStore freshnessStore)
        {
            return Fail("IndexFreshnessUnavailable", "The repository store cannot durably track source/index freshness; rollback was not applied.");
        }
        var affectedFiles = patch.Operations.Select(operation => NormalizePatchDirtyPath(root, operation.Path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (!await freshnessStore.TryMarkIndexFilesDirtyAsync(root, affectedFiles, cancellationToken))
        {
            return Fail("IndexDirty", "Rollback was not applied because this repository already has unreconciled source changes. Run 'devx index <root>' first.");
        }

        var (response, error) = _patchLifecycleService.Rollback(root, patch);
        if (error is not null)
        {
            if (error.Code is "PatchCommitOutcomeUncertain" or "PatchRollbackOutcomeUncertain")
                await freshnessStore.MarkIndexRepositoryDirtyAsync(root, affectedFiles, cancellationToken);
            else
                await freshnessStore.ClearDirtyIndexFilesAsync(root, affectedFiles, cancellationToken);
            return Fail(error.Code, error.Message);
        }

        var refreshFailure = _refreshIndexAfterEdit ? await RefreshIndexAsync(root, affectedFiles, cancellationToken) : null;
        if (refreshFailure is not null)
        {
            return Fail("IndexRefreshFailedDirty", $"Patch rollback source changes are applied, but indexed operations are blocked until reconciliation succeeds: {refreshFailure}. Run 'devx index <root>'.");
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }
}
