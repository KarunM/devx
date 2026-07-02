using System.Security.Cryptography;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
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

    private static bool IsNotIgnored(string path)
        => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => IgnoredDirectories.Contains(segment));

    private static string RelativePath(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
