using System.Security.Cryptography;
using System.Diagnostics;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DomainFile = DevX.Domain.File;

namespace DevX.Indexer;

public sealed record RepositoryIndexResult(int FilesIndexed, string? Reason);

public sealed class RepositoryIndexingService
{
    private static readonly string[] IgnoredDirectories = [".git", "bin", "obj", ".vs", ".idea", "node_modules", "dist", "build", "out", "coverage", ".venv", "venv", "__pycache__", ".terraform", "site-packages", ".mypy_cache", ".pytest_cache", ".eggs", ".tox", ".devx"];
    private readonly IRepositoryStore _repositoryStore;
    private readonly ISymbolStore _symbolStore;
    private readonly ILanguageParser _languageParser;

    public RepositoryIndexingService(IRepositoryStore repositoryStore)
        : this(repositoryStore, repositoryStore is ISymbolStore symbolStore ? symbolStore : throw new InvalidOperationException("Repository store must also support symbol storage."), new CompositeLanguageParser())
    {
    }

    public RepositoryIndexingService(IRepositoryStore repositoryStore, ISymbolStore symbolStore, ILanguageParser languageParser)
    {
        _repositoryStore = repositoryStore;
        _symbolStore = symbolStore;
        _languageParser = languageParser;
    }

    public async Task<RepositoryIndexResult> IndexAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var repository = new Repository(new RepositoryId(StableId(rootPath)), Path.GetFileName(rootPath), rootPath);
        await _repositoryStore.InitializeAsync(cancellationToken);
        await _repositoryStore.UpsertRepositoryAsync(repository, cancellationToken);
        var freshnessStore = _repositoryStore as IIndexFreshnessStore;
        var dirtyState = freshnessStore is null
            ? null
            : await freshnessStore.GetDirtyIndexRepositoryAsync(rootPath, cancellationToken);
        if (freshnessStore is not null)
        {
            await freshnessStore.MarkIndexRepositoryDirtyAsync(rootPath, dirtyState?.DirtyFiles ?? [], cancellationToken);
            if (dirtyState is not null)
            {
                // Reconcile previously fenced files from a clean slate so symbols removed by a
                // source edit cannot survive a successful full-index recovery.
                foreach (var dirtyFile in dirtyState.DirtyFiles)
                {
                    var dirtyPath = Path.GetFullPath(Path.Combine(rootPath, dirtyFile.Replace('\\', '/')));
                    if (!dirtyPath.StartsWith(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Dirty index path '{dirtyFile}' is outside the repository root.", nameof(repositoryPath));
                    }
                    await _repositoryStore.DeleteFileAsync(new FileId(StableId(dirtyPath)), cancellationToken);
                }
            }
        }

        var projects = RepositoryPathGuard.EnumerateFiles(rootPath, "*.csproj").Where(path => IsNotIgnored(rootPath, path)).ToList();
        var allFiles = RepositoryPathGuard.EnumerateFiles(rootPath).ToList();
        var considered = allFiles.Where(path => IsIndexable(path)
            && (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || projects.Count == 0
                || projects.Any(project => path.StartsWith(Path.GetDirectoryName(project) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))).ToList();
        var indexable = considered.Where(path => IsNotIgnored(rootPath, path)).ToList();

        foreach (var projectPath in projects)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileNameWithoutExtension(projectPath), RelativePath(rootPath, projectPath));
            await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

            foreach (var filePath in RepositoryPathGuard.EnumerateFiles(projectDirectory, "*.cs").Where(path => IsNotIgnored(rootPath, path)))
            {
                var file = new DomainFile(new FileId(StableId(filePath)), project.Id, RelativePath(rootPath, filePath));
                await _repositoryStore.UpsertFileAsync(file, cancellationToken);

                var parsed = _languageParser.Parse(filePath, await System.IO.File.ReadAllTextAsync(filePath, cancellationToken));
                var hasTopLevelProgram = parsed.Types.Any(type => type.Name == "Program" && type.Kind == "Class" && type.Methods.Count > 0);
                if (!string.IsNullOrWhiteSpace(parsed.Namespace) || hasTopLevelProgram || parsed.Types.Count > 0)
                {
                    var namespaceName = parsed.Namespace ?? string.Empty;
                    var namespaceId = new NamespaceId(StableId($"{filePath}:{namespaceName}"));
                    await _repositoryStore.UpsertNamespaceAsync(new Namespace(namespaceId, file.Id, namespaceName), cancellationToken);

                    foreach (var type in parsed.Types)
                    {
                        var typeId = new TypeId(StableId($"{filePath}:{namespaceName}:{type.Name}"));
                        await _repositoryStore.UpsertTypeAsync(new DevX.Domain.Type(typeId, namespaceId, type.Name), cancellationToken);
                        await _symbolStore.UpsertSymbolAsync(new Symbol(
                            StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}").ToString(),
                            repository.Id.Value.ToString(),
                            project.Id.Value.ToString(),
                            file.Id.Value.ToString(),
                            namespaceId.Value.ToString(),
                            type.Kind,
                            type.Name,
                            QualifiedName(namespaceName, type.Name),
                            null, type.StartLine, type.EndLine), cancellationToken);

                        foreach (var method in type.Methods)
                        {
                            await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{method.Name}:{method.Signature}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                method.IsConstructor ? "Constructor" : "Method",
                                method.Name,
                                QualifiedName(namespaceName, type.Name, method.Name),
                                method.Signature, method.StartLine, method.EndLine), cancellationToken);
                        }

                        await IndexCallersAsync(filePath, file.Id, namespaceName, type.Name, type.Methods, cancellationToken);

                        foreach (var property in type.Properties)
                        {
                            await _repositoryStore.UpsertPropertyAsync(new Property(new PropertyId(StableId($"{typeId}:{property.Name}")), typeId, property.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{property.Name}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                "Property",
                                property.Name,
                                QualifiedName(namespaceName, type.Name, property.Name),
                                property.Type, property.StartLine, property.EndLine), cancellationToken);
                        }

                        foreach (var field in type.Fields)
                        {
                            await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{field.Name}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                "Field",
                                field.Name,
                                QualifiedName(namespaceName, type.Name, field.Name),
                                field.Type, field.StartLine, field.EndLine), cancellationToken);
                        }
                    }
                }
            }
        }

        if (projects.Count == 0)
        {
            var looseCSharpFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.cs").Where(path => IsNotIgnored(rootPath, path)).ToList();
            if (looseCSharpFiles.Count > 0)
            {
                var projectPath = Path.Combine(rootPath, "<csharp>");
                var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
                await _repositoryStore.UpsertProjectAsync(project, cancellationToken);
                foreach (var filePath in looseCSharpFiles)
                {
                    var file = new DomainFile(new FileId(StableId(filePath)), project.Id, RelativePath(rootPath, filePath));
                    await _repositoryStore.UpsertFileAsync(file, cancellationToken);
                    var parsed = _languageParser.Parse(filePath, await System.IO.File.ReadAllTextAsync(filePath, cancellationToken));
                    var namespaceName = parsed.Namespace ?? string.Empty;
                    var nsId = new NamespaceId(StableId($"{filePath}:{namespaceName}"));
                    await _repositoryStore.UpsertNamespaceAsync(new Namespace(nsId, file.Id, namespaceName), cancellationToken);
                    foreach (var type in parsed.Types)
                    {
                        var typeId = new TypeId(StableId($"{filePath}:{namespaceName}:{type.Name}"));
                        await _repositoryStore.UpsertTypeAsync(new DevX.Domain.Type(typeId, nsId, type.Name), cancellationToken);
                        await _symbolStore.UpsertSymbolAsync(new Symbol(StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}").ToString(), repository.Id.Value.ToString(), project.Id.Value.ToString(), file.Id.Value.ToString(), nsId.Value.ToString(), type.Kind, type.Name, QualifiedName(namespaceName, type.Name), null, type.StartLine, type.EndLine), cancellationToken);
                        foreach (var method in type.Methods)
                        {
                            await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{method.Name}:{method.Signature}").ToString(), repository.Id.Value.ToString(), project.Id.Value.ToString(), file.Id.Value.ToString(), typeId.Value.ToString(), "Method", method.Name, QualifiedName(namespaceName, type.Name, method.Name), method.Signature, method.StartLine, method.EndLine), cancellationToken);
                        }
                        foreach (var property in type.Properties)
                        {
                            await _repositoryStore.UpsertPropertyAsync(new Property(new PropertyId(StableId($"{typeId}:{property.Name}")), typeId, property.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{property.Name}").ToString(), repository.Id.Value.ToString(), project.Id.Value.ToString(), file.Id.Value.ToString(), typeId.Value.ToString(), "Property", property.Name, QualifiedName(namespaceName, type.Name, property.Name), property.Type, property.StartLine, property.EndLine), cancellationToken);
                        }
                        foreach (var field in type.Fields)
                        {
                            await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{namespaceName}:{type.Name}:{field.Name}").ToString(), repository.Id.Value.ToString(), project.Id.Value.ToString(), file.Id.Value.ToString(), typeId.Value.ToString(), "Field", field.Name, QualifiedName(namespaceName, type.Name, field.Name), field.Type, field.StartLine, field.EndLine), cancellationToken);
                        }
                    }
                }
            }
        }

        await IndexPythonFilesAsync(rootPath, repository, cancellationToken);
        await IndexMarkdownFilesAsync(rootPath, repository, cancellationToken);
        await IndexJavaScriptFilesAsync(rootPath, repository, cancellationToken);
        await IndexJsonFilesAsync(rootPath, repository, cancellationToken);
        await IndexHtmlFilesAsync(rootPath, repository, cancellationToken);
        await IndexConfigFilesAsync(rootPath, repository, cancellationToken);
        if (freshnessStore is not null)
        {
            if (dirtyState is not null)
            {
                var indexedPaths = indexable.Select(path => RelativePath(rootPath, path).Replace('\\', '/'))
                    .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var dirtyFile in dirtyState.DirtyFiles.Where(path => !indexedPaths.Contains(path)))
                {
                    await RemoveFileAsync(rootPath, dirtyFile, cancellationToken);
                }
            }
            await freshnessStore.ClearDirtyIndexRepositoryAsync(rootPath, cancellationToken);
        }
        await RecordIndexedHeadAsync(rootPath, cancellationToken);
        return new RepositoryIndexResult(indexable.Count, indexable.Count == 0
            ? considered.Count == 0 ? "no files with an indexable extension found" : "all files ignored"
            : null);
    }

    /// <summary>
    /// Python has no project file (.csproj/.sln) to anchor discovery on, so every *.py file under the
    /// repository root is indexed directly under one synthetic project named after the repo. This runs
    /// unconditionally alongside the .csproj loop above (harmless no-op on a pure-C# repo, since
    /// EnumerateFiles simply returns nothing) and does not alter any C# indexing path or IDs.
    /// </summary>
    private async Task IndexPythonFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var pythonFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.py").Where(path => IsNotIgnored(rootPath, path)).ToList();
        if (pythonFiles.Count == 0)
        {
            return;
        }

        var projectPath = Path.Combine(rootPath, "<python>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

        foreach (var filePath in pythonFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    /// <summary>
    /// Markdown, like Python, has no project file to anchor discovery on, so every *.md/*.markdown/
    /// *.mdx file under the root is indexed under one synthetic "&lt;markdown&gt;" project. Kept separate
    /// from IndexPythonFilesAsync so the Python project id stays byte-identical and existing indexes
    /// do not shift. Reuses IndexPythonFileAsync, which is language-agnostic: it parses through the
    /// composite parser and emits whatever types/methods come back.
    /// </summary>
    private async Task IndexMarkdownFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var markdownFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.*")
            .Where(path => MarkdownSymbolReader.CanParse(path))
            .Where(path => IsNotIgnored(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (markdownFiles.Count == 0)
        {
            return;
        }

        var projectPath = Path.Combine(rootPath, "<markdown>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

        foreach (var filePath in markdownFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    /// <summary>
    /// JavaScript, like Python and Markdown, has no project file to anchor on, so every JS file under
    /// the root (minified *.min.js excluded) is indexed under one synthetic "&lt;javascript&gt;" project.
    /// </summary>
    private async Task IndexJavaScriptFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var javaScriptFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.*")
            .Where(path => JavaScriptSymbolReader.CanParse(path))
            .Where(path => !path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
            .Where(path => IsNotIgnored(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (javaScriptFiles.Count == 0)
        {
            return;
        }

        var projectPath = Path.Combine(rootPath, "<javascript>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

        foreach (var filePath in javaScriptFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    private async Task IndexJsonFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var jsonFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.json")
            .Where(path => IsNotIgnored(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (jsonFiles.Count == 0)
        {
            return;
        }

        var projectPath = Path.Combine(rootPath, "<json>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

        foreach (var filePath in jsonFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    /// <summary>Terraform/HCL, YAML and TOML files, under one synthetic "&lt;config&gt;" project.</summary>
    private async Task IndexConfigFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var configFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.*")
            .Where(StructuredSymbolReader.ConfigFile)
            .Where(path => IsNotIgnored(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (configFiles.Count == 0) return;

        var projectPath = Path.Combine(rootPath, "<config>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);
        foreach (var filePath in configFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    private async Task IndexHtmlFilesAsync(string rootPath, Repository repository, CancellationToken cancellationToken)
    {
        var htmlFiles = RepositoryPathGuard.EnumerateFiles(rootPath, "*.*")
            .Where(HtmlSymbolReader.CanParse)
            .Where(path => IsNotIgnored(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (htmlFiles.Count == 0) return;

        var projectPath = Path.Combine(rootPath, "<html>");
        var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
        await _repositoryStore.UpsertProjectAsync(project, cancellationToken);
        foreach (var filePath in htmlFiles)
        {
            await IndexPythonFileAsync(rootPath, repository.Id, project.Id, filePath, cancellationToken);
        }
    }

    private async Task IndexPythonFileAsync(string rootPath, RepositoryId repositoryId, ProjectId projectId, string filePath, CancellationToken cancellationToken)
    {
        var file = new DomainFile(new FileId(StableId(filePath)), projectId, RelativePath(rootPath, filePath));
        await _repositoryStore.UpsertFileAsync(file, cancellationToken);

        var relativePath = RelativePath(rootPath, filePath);
        var parsed = _languageParser.Parse(filePath, await System.IO.File.ReadAllTextAsync(filePath, cancellationToken));
        parsed = NormalizeStructuredNamespace(parsed, relativePath);
        if (string.IsNullOrWhiteSpace(parsed.Namespace))
        {
            return;
        }

        var namespaceId = new NamespaceId(StableId($"{filePath}:{parsed.Namespace}"));
        await _repositoryStore.UpsertNamespaceAsync(new Namespace(namespaceId, file.Id, parsed.Namespace!), cancellationToken);

        foreach (var type in parsed.Types)
        {
            var typeId = new TypeId(StableId($"{filePath}:{parsed.Namespace}:{type.Name}"));
            await _repositoryStore.UpsertTypeAsync(new DevX.Domain.Type(typeId, namespaceId, type.Name), cancellationToken);
            await _symbolStore.UpsertSymbolAsync(new Symbol(
                StableId($"{repositoryId.Value}:{projectId.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}").ToString(),
                repositoryId.Value.ToString(),
                projectId.Value.ToString(),
                file.Id.Value.ToString(),
                namespaceId.Value.ToString(),
                type.Kind,
                type.Name,
                QualifiedName(parsed.Namespace, type.Name),
                null, type.StartLine, type.EndLine), cancellationToken);

            foreach (var method in type.Methods)
            {
                await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                await _symbolStore.UpsertSymbolAsync(new Symbol(
                    StableId($"{repositoryId.Value}:{projectId.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}:{method.Name}:{method.Signature}").ToString(),
                    repositoryId.Value.ToString(),
                    projectId.Value.ToString(),
                    file.Id.Value.ToString(),
                    typeId.Value.ToString(),
                    method.IsConstructor ? "Constructor" : "Method",
                    method.Name,
                    QualifiedName(parsed.Namespace, type.Name, method.Name),
                    method.Signature, method.StartLine, method.EndLine), cancellationToken);
            }

            foreach (var field in type.Fields)
            {
                await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                await _symbolStore.UpsertSymbolAsync(new Symbol(
                    StableId($"{repositoryId.Value}:{projectId.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}:{field.Name}").ToString(),
                    repositoryId.Value.ToString(),
                    projectId.Value.ToString(),
                    file.Id.Value.ToString(),
                    typeId.Value.ToString(),
                    "Field",
                    field.Name,
                    QualifiedName(parsed.Namespace, type.Name, field.Name),
                    field.Type, field.StartLine, field.EndLine), cancellationToken);
            }

            // Caller/callee graph is deferred: Python has no Roslyn-equivalent compiler API in this
            // codebase, and a regex/AST-only call-site resolver would be approximate (no type
            // resolution, so `obj.method()` calls cannot be reliably matched to a target symbol).
            // Shipping that would violate the brief's "no approximate call graph" instruction, so
            // `callers`/`callees`/`references` remain empty for Python symbols until a real resolver
            // is built.
        }
    }

    public async Task ReindexFileAsync(string repositoryPath, string filePath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        if (!RepositoryPathGuard.TryResolve(rootPath, filePath, out var absolutePath, out var pathError))
            throw new ArgumentException(pathError, nameof(filePath));
        var relativePath = Path.GetRelativePath(rootPath, absolutePath).Replace('\\', '/');

        if (!System.IO.File.Exists(absolutePath))
        {
            throw new FileNotFoundException("File not found.", absolutePath);
        }

        var repositoryId = new RepositoryId(StableId(rootPath));
        var fileId = new FileId(StableId(absolutePath));
        await _repositoryStore.DeleteFileAsync(fileId, cancellationToken);

        if (!absolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !StructuredSymbolReader.CanParse(absolutePath))
        {
            // Unsupported extensions have no indexed source representation. DeleteFileAsync
            // above removes any stale entry with this stable file id; only then is this one
            // path safe to clear (a repository-wide dirty fence remains set).
            if (_repositoryStore is IIndexFreshnessStore fileFreshnessStore)
            {
                await fileFreshnessStore.ClearDirtyIndexFilesAsync(rootPath, [relativePath], cancellationToken);
            }
            return;
        }

        var projectPath = RepositoryPathGuard.EnumerateFiles(rootPath, "*.csproj")
            .FirstOrDefault(p => absolutePath.StartsWith(Path.GetDirectoryName(p)!, StringComparison.OrdinalIgnoreCase));

        ProjectId projectId;
        Project project;
        if (projectPath is not null)
        {
            projectId = new ProjectId(StableId(projectPath));
            project = new Project(projectId, repositoryId, Path.GetFileNameWithoutExtension(projectPath), RelativePath(rootPath, projectPath));
        }
        else if (StructuredSymbolReader.CanParse(absolutePath))
        {
            // Python and Markdown have no .csproj to anchor a project on. Mirror the synthetic
            // project IndexAsync creates during a full index -- "<markdown>" for docs, "<python>"
            // otherwise -- so incremental updates land under the same project id instead of
            // failing outright or splitting a file across two projects.
            var syntheticProjectPath = Path.Combine(
                rootPath,
                MarkdownSymbolReader.CanParse(absolutePath) ? "<markdown>"
                    : JavaScriptSymbolReader.CanParse(absolutePath) ? "<javascript>"
                    : JsonSymbolReader.CanParse(absolutePath) ? "<json>"
                    : HtmlSymbolReader.CanParse(absolutePath) ? "<html>"
                    : StructuredSymbolReader.ConfigFile(absolutePath) ? "<config>" : "<python>");
            projectId = new ProjectId(StableId(syntheticProjectPath));
            project = new Project(projectId, repositoryId, Path.GetFileName(rootPath), RelativePath(rootPath, rootPath));
            await _repositoryStore.UpsertProjectAsync(project, cancellationToken);
        }
        else if (absolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // Loose C# files are supported by a full repository index, but they have no
            // incrementally resolvable project here. Keep their freshness marker until
            // that full reconciliation runs.
            throw new InvalidOperationException("Could not determine project for file.");
        }
        else throw new InvalidOperationException("Could not determine project for file.");

        var file = new DomainFile(fileId, project.Id, RelativePath(rootPath, absolutePath));
        await _repositoryStore.UpsertFileAsync(file, cancellationToken);

        var parsed = _languageParser.Parse(absolutePath, await System.IO.File.ReadAllTextAsync(absolutePath, cancellationToken));
        parsed = NormalizeStructuredNamespace(parsed, RelativePath(rootPath, absolutePath));
        if (absolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(parsed.Namespace))
        {
            var namespaceName = parsed.Namespace ?? string.Empty;
            var namespaceId = new NamespaceId(StableId($"{absolutePath}:{namespaceName}"));
            await _repositoryStore.UpsertNamespaceAsync(new Namespace(namespaceId, file.Id, namespaceName), cancellationToken);

            foreach (var type in parsed.Types)
            {
                var typeId = new TypeId(StableId($"{absolutePath}:{namespaceName}:{type.Name}"));
                await _repositoryStore.UpsertTypeAsync(new DevX.Domain.Type(typeId, namespaceId, type.Name), cancellationToken);
                await _symbolStore.UpsertSymbolAsync(new Symbol(
                    StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{namespaceName}:{type.Name}").ToString(),
                    repositoryId.Value.ToString(),
                    projectId.Value.ToString(),
                    fileId.Value.ToString(),
                    namespaceId.Value.ToString(),
                    type.Kind,
                    type.Name,
                    QualifiedName(namespaceName, type.Name),
                    null, type.StartLine, type.EndLine), cancellationToken);

                foreach (var method in type.Methods)
                {
                    await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{namespaceName}:{type.Name}:{method.Name}:{method.Signature}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        method.IsConstructor ? "Constructor" : "Method",
                        method.Name,
                        QualifiedName(namespaceName, type.Name, method.Name),
                        method.Signature, method.StartLine, method.EndLine), cancellationToken);
                }

                await IndexCallersAsync(absolutePath, file.Id, namespaceName, type.Name, type.Methods, cancellationToken);

                foreach (var property in type.Properties)
                {
                    await _repositoryStore.UpsertPropertyAsync(new Property(new PropertyId(StableId($"{typeId}:{property.Name}")), typeId, property.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{namespaceName}:{type.Name}:{property.Name}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        "Property",
                        property.Name,
                        QualifiedName(namespaceName, type.Name, property.Name),
                        property.Type, property.StartLine, property.EndLine), cancellationToken);
                }

                foreach (var field in type.Fields)
                {
                    await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{namespaceName}:{type.Name}:{field.Name}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        "Field",
                        field.Name,
                        QualifiedName(namespaceName, type.Name, field.Name),
                        field.Type, field.StartLine, field.EndLine), cancellationToken);
                }
            }
        }
        if (_repositoryStore is IIndexFreshnessStore freshnessStore)
        {
            await freshnessStore.ClearDirtyIndexFilesAsync(rootPath, [relativePath], cancellationToken);
        }
    }

    public async Task<IndexRefreshResult> ReindexFilesAsync(string repositoryPath, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        var nestedRepositories = NestedRepositoryGuard.Find(Path.GetFullPath(repositoryPath));
        var normalized = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => path.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !NestedRepositoryGuard.IsInside(
                Path.IsPathRooted(path) ? Path.GetRelativePath(Path.GetFullPath(repositoryPath), path).Replace('\\', '/') : path, nestedRepositories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_repositoryStore is IIndexFreshnessStore freshnessStore)
        {
            await freshnessStore.MarkIndexFilesDirtyAsync(repositoryPath, normalized, cancellationToken);
        }

        if (normalized.Length == 0)
        {
            return new IndexRefreshResult("files", 0, 0, [], false);
        }

        var updated = 0;
        var removed = 0;
        var full = false;
        var skippedFiles = new List<SkippedFile>();
        foreach (var path in normalized)
        {
            var absolute = Path.GetFullPath(Path.Combine(repositoryPath, path));
            if (!absolute.StartsWith(Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                skippedFiles.Add(new SkippedFile(path, "ERROR: File path must be inside the repository root."));
                continue;
            }
            if (!System.IO.File.Exists(absolute))
            {
                // A file the index holds that is gone from disk is a deletion: drop it. A path the index
                // never held is a typo or a stale name: report it, but do not leave it marked dirty.
                if (await _repositoryStore.GetFileSnapshotAsync(new FileId(StableId(absolute)).Value.ToString(), cancellationToken) is not null)
                {
                    await RemoveFileAsync(repositoryPath, path, cancellationToken);
                    removed++;
                    continue;
                }
                if (_repositoryStore is IIndexFreshnessStore missingFreshnessStore)
                    await missingFreshnessStore.ClearDirtyIndexFilesAsync(repositoryPath, [path], cancellationToken);
                skippedFiles.Add(new SkippedFile(path, "ERROR: file missing"));
                continue;
            }
            if (RequiresFullReindex(path))
            {
                if (!full) await IndexAsync(repositoryPath, cancellationToken);
                full = true;
                updated++;
                continue;
            }
            if (!SupportsIncrementalFile(path))
            {
                try
                {
                    // ReindexFileAsync first deletes any stale indexed row. For truly
                    // unsupported extensions it then clears this path's fence; supported
                    // loose C# still throws and remains fenced for full-index recovery.
                    await ReindexFileAsync(repositoryPath, path, cancellationToken);
                    skippedFiles.Add(new SkippedFile(path, $"Unsupported file type for incremental indexing: '{Path.GetExtension(path)}'. No indexed entry was retained."));
                }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
                {
                    skippedFiles.Add(new SkippedFile(path, $"ERROR: {error.Message}"));
                }
                continue;
            }

            try
            {
                await ReindexFileAsync(repositoryPath, path, cancellationToken);
                updated++;
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
            {
                skippedFiles.Add(new SkippedFile(path, $"ERROR: {error.Message}"));
            }
        }

        return new IndexRefreshResult("files", updated, skippedFiles.Count, normalized, full, skippedFiles, removed);
    }

    /// <summary>
    /// Drop a file that no longer exists from the index. Symbols, content-search entries, and
    /// other file-owned rows follow it through ON DELETE CASCADE; freshness marks are cleared.
    /// </summary>
    public async Task RemoveFileAsync(string repositoryPath, string filePath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var absolutePath = Path.GetFullPath(Path.Combine(rootPath, filePath.Replace('\\', '/')));
        if (!absolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File path must be inside the repository root.", nameof(filePath));
        }

        await _repositoryStore.DeleteFileAsync(new FileId(StableId(absolutePath)), cancellationToken);
        if (_repositoryStore is IIndexFreshnessStore freshnessStore)
        {
            await freshnessStore.ClearDirtyIndexFilesAsync(rootPath, [filePath.Replace('\\', '/')], cancellationToken);
        }
    }

    /// <summary>
    /// How many files the index holds for the repository rooted at <paramref name="repositoryPath"/>,
    /// 0 when that path has never been indexed. The repository id is derived exactly as
    /// <see cref="IndexAsync(string, CancellationToken)"/> derives it, so the count is about the
    /// index a whole-repository run would rewrite -- which is what lets a caller skip that run.
    /// </summary>
    public Task<int> CountIndexedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
        => _repositoryStore.CountIndexedFilesAsync(new RepositoryId(StableId(Path.GetFullPath(repositoryPath))), cancellationToken);

    public async Task<IndexRefreshResult> ReindexChangedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default, bool reconcileMissingFiles = true)
    {
        var removedFiles = reconcileMissingFiles ? await ReconcileMissingIndexedFilesAsync(repositoryPath, cancellationToken) : 0;
        if (await RequiresFullReindexAsync(repositoryPath, cancellationToken))
        {
            // A repository-wide dirty fence (an interrupted full index or an uncertain patch) is only
            // cleared by a full run; per-file work cannot lift it.
            var full = await IndexAsync(repositoryPath, cancellationToken);
            return new IndexRefreshResult("changed", full.FilesIndexed, 0, [], true, RemovedFiles: removedFiles);
        }

        var changedFiles = await GetPendingFilesAsync(repositoryPath, cancellationToken);
        if (changedFiles.Count == 0)
        {
            await RecordIndexedHeadAsync(Path.GetFullPath(repositoryPath), cancellationToken);
            return new IndexRefreshResult("changed", 0, 0, [], false, RemovedFiles: removedFiles);
        }

        var result = await ReindexFilesAsync(repositoryPath, changedFiles, cancellationToken);
        if (!result.SkippedFilePaths.Any(skipped => skipped.Reason.StartsWith("ERROR:", StringComparison.Ordinal) && skipped.Reason != "ERROR: file missing"))
        {
            await RecordIndexedHeadAsync(Path.GetFullPath(repositoryPath), cancellationToken);
        }
        return result with { Mode = "changed", RemovedFiles = result.RemovedFiles + removedFiles };
    }

    /// <summary>True when the repository carries a repository-wide dirty fence that only a full index clears.</summary>
    public async Task<bool> RequiresFullReindexAsync(string repositoryPath, CancellationToken cancellationToken = default)
        => _repositoryStore is IIndexFreshnessStore freshness
            && (await freshness.GetDirtyIndexRepositoryAsync(Path.GetFullPath(repositoryPath), cancellationToken))?.RequiresFullReindex == true;

    /// <summary>
    /// Everything that needs re-indexing: files git shows as changed, files still fenced as dirty
    /// (committed since, so git no longer shows them), and files that arrived since the last indexed
    /// commit through pull, merge or checkout.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPendingFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var pending = new List<string>(await GetChangedFilesAsync(rootPath, cancellationToken));
        if (_repositoryStore is IIndexFreshnessStore freshness
            && await freshness.GetDirtyIndexRepositoryAsync(rootPath, cancellationToken) is { } dirty)
        {
            pending.AddRange(dirty.DirtyFiles);
        }
        pending.AddRange(await GetCommitDriftFilesAsync(rootPath, cancellationToken));
        return pending.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string IndexedHeadFile(string rootPath) => Path.Combine(rootPath, ".devx", "indexed-head");

    private static async Task<string?> RunGitAsync(string rootPath, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task RecordIndexedHeadAsync(string rootPath, CancellationToken cancellationToken)
    {
        var head = (await RunGitAsync(rootPath, "rev-parse HEAD", cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(head)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, ".devx"));
            await System.IO.File.WriteAllTextAsync(IndexedHeadFile(rootPath), head, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Read-only state directory: the next run simply reconciles again.
        }
    }

    private static bool IsReindexableKind(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || StructuredSymbolReader.CanParse(path)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>> GetCommitDriftFilesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var head = (await RunGitAsync(rootPath, "rev-parse HEAD", cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(head)) return [];
        var recorded = IndexedHeadFile(rootPath) is var file && System.IO.File.Exists(file)
            ? (await System.IO.File.ReadAllTextAsync(file, cancellationToken)).Trim()
            : null;
        if (recorded == head) return [];

        string? names;
        if (!string.IsNullOrEmpty(recorded))
        {
            names = await RunGitAsync(rootPath, $"diff --name-only --no-renames -z {recorded} {head}", cancellationToken);
            if (names is not null)
            {
                var nestedRepositories = NestedRepositoryGuard.Find(rootPath);
                return names.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Replace('\\', '/'))
                    .Where(path => !NestedRepositoryGuard.IsInside(path, nestedRepositories))
                    .Where(IsReindexableKind)
                    .ToArray();
            }
        }

        // No usable recorded commit (an index from before commits were recorded, or a commit that was
        // rebased away): compare the working tree with the index by path instead.
        names = await RunGitAsync(rootPath, "ls-files -z --cached --others --exclude-standard", cancellationToken);
        if (names is null) return [];
        var nested = NestedRepositoryGuard.Find(rootPath);
        var indexed = (await _repositoryStore.GetIndexedFilePathsAsync(rootPath, cancellationToken))
            .Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => IsReindexableKind(path) && !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Split('/').Any(segment => IgnoredDirectories.Contains(segment)))
            .Where(path => !NestedRepositoryGuard.IsInside(path, nested))
            .Where(path => !indexed.Contains(path) && System.IO.File.Exists(Path.Combine(rootPath, path)))
            .ToArray();
    }

    public async Task<int> ReconcileMissingIndexedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryPath);
        var indexedPaths = await _repositoryStore.GetIndexedFilePathsAsync(root, cancellationToken);
        var removed = 0;
        foreach (var file in indexedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolute = Path.Combine(root, file.Replace('\\', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(absolute)) continue;
            await RemoveFileAsync(root, file, cancellationToken);
            removed++;
        }
        return removed;
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var startInfo = new ProcessStartInfo("git", "status --porcelain")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Failed to inspect changed files." : error.Trim());
        }

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseChangedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || StructuredSymbolReader.CanParse(path)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ParseChangedPath(string statusLine)
    {
        var payload = statusLine.Length > 3 ? statusLine[3..].Trim() : string.Empty;
        if (payload.Contains(" -> ", StringComparison.Ordinal))
        {
            return payload[(payload.IndexOf(" -> ", StringComparison.Ordinal) + 4)..].Replace('\\', '/');
        }

        return payload.Replace('\\', '/');
    }

    private static bool SupportsIncrementalFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || StructuredSymbolReader.CanParse(path);

    private static ParsedFile NormalizeStructuredNamespace(ParsedFile parsed, string relativePath)
    {
        var normalizedNamespace = PythonSymbolReader.CanParse(relativePath)
            ? PythonSymbolReader.ModuleNamespaceFromPath(relativePath)
            : MarkdownSymbolReader.CanParse(relativePath)
                ? MarkdownSymbolReader.DocumentNamespaceFromPath(relativePath)
                : parsed.Namespace;
        return parsed with { FilePath = relativePath, Namespace = normalizedNamespace };
    }

    private static bool RequiresFullReindex(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var projectBacked = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Any();
        var ignoredDirectories = projectBacked ? IgnoredDirectories.Where(name => name is not "build" and not ".devx") : IgnoredDirectories;
        if (relative.Split('/').Any(segment => ignoredDirectories.Contains(segment))) return false;
        if (projectBacked) return true;
        var ignore = Path.Combine(root, ".gitignore");
        if (!System.IO.File.Exists(ignore)) return true;
        return !System.IO.File.ReadAllLines(ignore).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Any(pattern =>
            {
                pattern = pattern.TrimStart('/').TrimEnd('/');
                var wildcard = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
                var expression = pattern.Contains('/') ? $"^{wildcard}($|/)" : $"(^|/){wildcard}($|/)";
                return System.Text.RegularExpressions.Regex.IsMatch(relative, expression, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            });
    }

    private static bool IsIndexable(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || PythonSymbolReader.CanParse(path) || MarkdownSymbolReader.CanParse(path)
            || JavaScriptSymbolReader.CanParse(path) || JsonSymbolReader.CanParse(path) || HtmlSymbolReader.CanParse(path)
            || StructuredSymbolReader.ConfigFile(path);

    private static string RelativePath(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private async Task IndexCallersAsync(string filePath, FileId fileId, string @namespace, string typeName, IReadOnlyList<ParsedMethod> methods, CancellationToken cancellationToken)
    {
        var sourceText = await System.IO.File.ReadAllTextAsync(filePath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        foreach (var methodNode in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            var callerName = methodNode switch
            {
                MethodDeclarationSyntax m => m.Identifier.ValueText,
                ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                _ => string.Empty
            };
            var callerQualified = string.IsNullOrWhiteSpace(@namespace) ? $"{typeName}.{callerName}" : $"{@namespace}.{typeName}.{callerName}";
            var callerSymbol = await FindCallerSymbolAsync(fileId, @namespace, typeName, callerName, cancellationToken);
            if (callerSymbol is null)
            {
                continue;
            }

            foreach (var invocation in methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var targetName = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    continue;
                }

                var targetSymbols = await _symbolStore.FindSymbolsByNameAsync(targetName!, cancellationToken);
                foreach (var target in targetSymbols.Where(symbol => symbol.Kind is "Method" or "Constructor"))
                {
                    await _repositoryStore.UpsertReferenceAsync(new Reference(
                        new ReferenceId(StableId($"{callerSymbol.Id}:{target.Id}:{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")),
                        fileId,
                        target.Id,
                        callerSymbol.Id,
                        invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1), cancellationToken);
                }
            }
        }
    }

    private async Task<Symbol?> FindCallerSymbolAsync(FileId fileId, string @namespace, string typeName, string callerName, CancellationToken cancellationToken)
    {
        var qualified = QualifiedName(@namespace, typeName, callerName);
        var matches = await _symbolStore.FindSymbolsByQualifiedNameAsync(qualified, cancellationToken);
        return matches.FirstOrDefault(symbol => symbol.FileId == fileId.Value.ToString() && symbol.Kind is "Method" or "Constructor");
    }

    private static string QualifiedName(string? @namespace, params string[] parts)
        => string.Join('.', (string.IsNullOrWhiteSpace(@namespace) ? parts : [@namespace, .. parts]));

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}

public sealed record IndexRefreshResult(
    string Mode,
    int UpdatedFiles,
    int SkippedFiles,
    IReadOnlyList<string> RequestedFiles,
    bool PerformedFullReindex,
    IReadOnlyList<SkippedFile>? SkippedFilePaths = null,
    int RemovedFiles = 0)
{
    public IReadOnlyList<SkippedFile> SkippedFilePaths { get; init; } = SkippedFilePaths ?? [];
}

public sealed record SkippedFile(string Path, string Reason);
