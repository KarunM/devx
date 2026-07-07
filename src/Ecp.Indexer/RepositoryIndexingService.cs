using System.Security.Cryptography;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DomainFile = Ecp.Domain.File;

namespace Ecp.Indexer;

public sealed class RepositoryIndexingService
{
    private static readonly string[] IgnoredDirectories = [".git", "bin", "obj", ".vs", ".idea", "node_modules", "dist", "out", "coverage"];
    private readonly IRepositoryStore _repositoryStore;
    private readonly ISymbolStore _symbolStore;
    private readonly ILanguageParser _languageParser;

    public RepositoryIndexingService(IRepositoryStore repositoryStore)
        : this(repositoryStore, repositoryStore is ISymbolStore symbolStore ? symbolStore : throw new InvalidOperationException("Repository store must also support symbol storage."), new CSharpRoslynParser())
    {
    }

    public RepositoryIndexingService(IRepositoryStore repositoryStore, ISymbolStore symbolStore, ILanguageParser languageParser)
    {
        _repositoryStore = repositoryStore;
        _symbolStore = symbolStore;
        _languageParser = languageParser;
    }

    public async Task IndexAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var repository = new Repository(new RepositoryId(StableId(rootPath)), Path.GetFileName(rootPath), rootPath);
        await _repositoryStore.InitializeAsync(cancellationToken);
        await _repositoryStore.UpsertRepositoryAsync(repository, cancellationToken);

        foreach (var projectPath in Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories).Where(IsNotIgnored))
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var project = new Project(new ProjectId(StableId(projectPath)), repository.Id, Path.GetFileNameWithoutExtension(projectPath), RelativePath(rootPath, projectPath));
            await _repositoryStore.UpsertProjectAsync(project, cancellationToken);

            foreach (var filePath in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories).Where(IsNotIgnored))
            {
                var file = new DomainFile(new FileId(StableId(filePath)), project.Id, RelativePath(rootPath, filePath));
                await _repositoryStore.UpsertFileAsync(file, cancellationToken);

                var parsed = _languageParser.Parse(filePath, await System.IO.File.ReadAllTextAsync(filePath, cancellationToken));
                if (!string.IsNullOrWhiteSpace(parsed.Namespace))
                {
                    var namespaceId = new NamespaceId(StableId($"{filePath}:{parsed.Namespace}"));
                    await _repositoryStore.UpsertNamespaceAsync(new Namespace(namespaceId, file.Id, parsed.Namespace!), cancellationToken);

                    foreach (var type in parsed.Types)
                    {
                        var typeId = new TypeId(StableId($"{filePath}:{parsed.Namespace}:{type.Name}"));
                        await _repositoryStore.UpsertTypeAsync(new Ecp.Domain.Type(typeId, namespaceId, type.Name), cancellationToken);
                        await _symbolStore.UpsertSymbolAsync(new Symbol(
                            StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}").ToString(),
                            repository.Id.Value.ToString(),
                            project.Id.Value.ToString(),
                            file.Id.Value.ToString(),
                            namespaceId.Value.ToString(),
                            type.Kind,
                            type.Name,
                            $"{parsed.Namespace}.{type.Name}",
                            null), cancellationToken);

                    foreach (var method in type.Methods)
                    {
                        await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}:{method.Name}:{method.Signature}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                method.IsConstructor ? "Constructor" : "Method",
                                method.Name,
                                $"{parsed.Namespace}.{type.Name}.{method.Name}",
                                method.Signature), cancellationToken);
                    }

                    await IndexCallersAsync(filePath, file.Id, parsed.Namespace, type.Name, type.Methods, cancellationToken);

                    foreach (var property in type.Properties)
                    {
                            await _repositoryStore.UpsertPropertyAsync(new Property(new PropertyId(StableId($"{typeId}:{property.Name}")), typeId, property.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}:{property.Name}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                "Property",
                                property.Name,
                                $"{parsed.Namespace}.{type.Name}.{property.Name}",
                                property.Type), cancellationToken);
                        }

                        foreach (var field in type.Fields)
                        {
                            await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                            await _symbolStore.UpsertSymbolAsync(new Symbol(
                                StableId($"{repository.Id.Value}:{project.Id.Value}:{file.Id.Value}:{parsed.Namespace}:{type.Name}:{field.Name}").ToString(),
                                repository.Id.Value.ToString(),
                                project.Id.Value.ToString(),
                                file.Id.Value.ToString(),
                                typeId.Value.ToString(),
                                "Field",
                                field.Name,
                                $"{parsed.Namespace}.{type.Name}.{field.Name}",
                                field.Type), cancellationToken);
                        }
                    }
                }
            }
        }
    }

    public async Task ReindexFileAsync(string repositoryPath, string filePath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetFullPath(repositoryPath);
        var relativePath = filePath.Replace('\\', '/');
        var absolutePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));

        if (!absolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File path must be inside the repository root.", nameof(filePath));
        }

        if (!System.IO.File.Exists(absolutePath))
        {
            throw new FileNotFoundException("File not found.", absolutePath);
        }

        var repositoryId = new RepositoryId(StableId(rootPath));
        var fileId = new FileId(StableId(absolutePath));
        await _repositoryStore.DeleteFileAsync(fileId, cancellationToken);

        var projectPath = Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(p => absolutePath.StartsWith(Path.GetDirectoryName(p)!, StringComparison.OrdinalIgnoreCase));
        if (projectPath is null)
        {
            throw new InvalidOperationException("Could not determine project for file.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectId = new ProjectId(StableId(projectPath));
        var project = new Project(projectId, repositoryId, Path.GetFileNameWithoutExtension(projectPath), RelativePath(rootPath, projectPath));
        var file = new DomainFile(fileId, project.Id, RelativePath(rootPath, absolutePath));
        await _repositoryStore.UpsertFileAsync(file, cancellationToken);

        var parsed = _languageParser.Parse(absolutePath, await System.IO.File.ReadAllTextAsync(absolutePath, cancellationToken));
        if (!string.IsNullOrWhiteSpace(parsed.Namespace))
        {
            var namespaceId = new NamespaceId(StableId($"{absolutePath}:{parsed.Namespace}"));
            await _repositoryStore.UpsertNamespaceAsync(new Namespace(namespaceId, file.Id, parsed.Namespace!), cancellationToken);

            foreach (var type in parsed.Types)
            {
                var typeId = new TypeId(StableId($"{absolutePath}:{parsed.Namespace}:{type.Name}"));
                await _repositoryStore.UpsertTypeAsync(new Ecp.Domain.Type(typeId, namespaceId, type.Name), cancellationToken);
                await _symbolStore.UpsertSymbolAsync(new Symbol(
                    StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{parsed.Namespace}:{type.Name}").ToString(),
                    repositoryId.Value.ToString(),
                    projectId.Value.ToString(),
                    fileId.Value.ToString(),
                    namespaceId.Value.ToString(),
                    type.Kind,
                    type.Name,
                    $"{parsed.Namespace}.{type.Name}",
                    null), cancellationToken);

                foreach (var method in type.Methods)
                {
                    await _repositoryStore.UpsertMethodAsync(new Method(new MethodId(StableId($"{typeId}:{method.Name}:{method.Signature}")), typeId, method.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{parsed.Namespace}:{type.Name}:{method.Name}:{method.Signature}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        method.IsConstructor ? "Constructor" : "Method",
                        method.Name,
                        $"{parsed.Namespace}.{type.Name}.{method.Name}",
                        method.Signature), cancellationToken);
                }

                await IndexCallersAsync(absolutePath, file.Id, parsed.Namespace, type.Name, type.Methods, cancellationToken);

                foreach (var property in type.Properties)
                {
                    await _repositoryStore.UpsertPropertyAsync(new Property(new PropertyId(StableId($"{typeId}:{property.Name}")), typeId, property.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{parsed.Namespace}:{type.Name}:{property.Name}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        "Property",
                        property.Name,
                        $"{parsed.Namespace}.{type.Name}.{property.Name}",
                        property.Type), cancellationToken);
                }

                foreach (var field in type.Fields)
                {
                    await _repositoryStore.UpsertFieldAsync(new Field(new FieldId(StableId($"{typeId}:{field.Name}")), typeId, field.Name), cancellationToken);
                    await _symbolStore.UpsertSymbolAsync(new Symbol(
                        StableId($"{repositoryId.Value}:{projectId.Value}:{fileId.Value}:{parsed.Namespace}:{type.Name}:{field.Name}").ToString(),
                        repositoryId.Value.ToString(),
                        projectId.Value.ToString(),
                        fileId.Value.ToString(),
                        typeId.Value.ToString(),
                        "Field",
                        field.Name,
                        $"{parsed.Namespace}.{type.Name}.{field.Name}",
                        field.Type), cancellationToken);
                }
            }
        }
    }

    private static bool IsNotIgnored(string path)
        => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => IgnoredDirectories.Contains(segment));

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
        var qualified = string.IsNullOrWhiteSpace(@namespace) ? $"{typeName}.{callerName}" : $"{@namespace}.{typeName}.{callerName}";
        var matches = await _symbolStore.FindSymbolsByQualifiedNameAsync(qualified, cancellationToken);
        return matches.FirstOrDefault(symbol => symbol.FileId == fileId.Value.ToString() && symbol.Kind is "Method" or "Constructor");
    }

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
