using Microsoft.Data.Sqlite;
using DevX.Domain;
using DomainFile = DevX.Domain.File;
using DomainType = DevX.Domain.Type;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevX.Infrastructure.Storage.SQLite;

public sealed partial class SqliteStorageInitializer
{
    private static Task<TypeSummary?> BuildTypeSummaryAsync((string Id, string Name, string Namespace, string FilePath, string Project, string Repository, string RootPath) row, TypeDeclarationSyntax typeNode, string sourceText, CancellationToken cancellationToken)
    {
        var lineSpan = typeNode.GetLocation().GetLineSpan();
        var ns = row.Namespace;
        var qualifiedName = string.IsNullOrWhiteSpace(ns) ? row.Name : $"{ns}.{row.Name}";
        var methods = typeNode.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => new FileMethodSnapshot(Guid.NewGuid().ToString(), m.Identifier.ValueText, row.Name, ns))
            .ToList();
        var ctors = typeNode.Members.OfType<ConstructorDeclarationSyntax>()
            .Select(c => new FileMethodSnapshot(Guid.NewGuid().ToString(), c.Identifier.ValueText, row.Name, ns))
            .ToList();
        var properties = typeNode.Members.OfType<PropertyDeclarationSyntax>()
            .Select(p => new FilePropertySnapshot(Guid.NewGuid().ToString(), p.Identifier.ValueText, row.Name, ns))
            .ToList();
        var fields = typeNode.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => new FileFieldSnapshot(Guid.NewGuid().ToString(), v.Identifier.ValueText, row.Name, ns)))
            .ToList();

        TypeSummary summary = new(
            row.Id,
            row.Name,
            qualifiedName,
            typeNode.Kind().ToString().Replace("Declaration", "", StringComparison.OrdinalIgnoreCase),
            ns,
            row.Project,
            row.FilePath,
            null,
            [],
            typeNode.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [],
            GetVisibility(typeNode.Modifiers),
            ctors,
            properties,
            fields,
            methods,
            [],
            sourceText.Split('\n').Length,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.EndLinePosition.Line + 1);
        return Task.FromResult<TypeSummary?>(summary);
    }

    private static TypeSummary BuildPythonTypeSummary(
        (string Id, string Name, string Namespace, string FilePath, string Project, string Repository, string RootPath) row,
        ParsedType type,
        string sourceText)
    {
        var methods = type.Methods
            .Where(method => !method.IsConstructor)
            .Select(method => new FileMethodSnapshot(Guid.NewGuid().ToString(), method.Name, type.Name, row.Namespace))
            .ToList();
        var constructors = type.Methods
            .Where(method => method.IsConstructor)
            .Select(method => new FileMethodSnapshot(Guid.NewGuid().ToString(), method.Name, type.Name, row.Namespace))
            .ToList();
        var properties = type.Properties
            .Select(property => new FilePropertySnapshot(Guid.NewGuid().ToString(), property.Name, type.Name, row.Namespace))
            .ToList();
        var fields = type.Fields
            .Select(field => new FileFieldSnapshot(Guid.NewGuid().ToString(), field.Name, type.Name, row.Namespace))
            .ToList();

        return new TypeSummary(
            row.Id,
            type.Name,
            string.IsNullOrWhiteSpace(row.Namespace) ? type.Name : $"{row.Namespace}.{type.Name}",
            type.Kind,
            row.Namespace,
            row.Project,
            row.FilePath,
            null,
            [],
            [],
            type.Visibility,
            constructors,
            properties,
            fields,
            methods,
            [],
            sourceText.Split('\n').Length,
            type.StartLine,
            type.EndLine);
    }

    public async Task<int> CountIndexedFilesAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            WHERE p.repository_id = $repository_id;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId.Value.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<Repository?> GetRepositoryByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, root_path
            FROM repositories
            WHERE root_path = $root_path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root_path", rootPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Repository(
            new RepositoryId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2));
    }

    public async Task<string?> GetRepositoryRootPathAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_path
            FROM repositories
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", repositoryId.Value.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }

    public async Task DeleteFileAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fileId.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Symbol>> FindSymbolsAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name, s.signature,
                   s.start_line, s.end_line, f.path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            WHERE s.{columnName} = $value
            ORDER BY s.qualified_name;
            """;
        command.Parameters.AddWithValue("$value", value);

        var results = new List<Symbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Symbol(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<T>> QueryListAsync<T>(SqliteConnection connection, string commandText, string fileId, CancellationToken cancellationToken, Func<SqliteDataReader, T> map)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$file_id", fileId);
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }
        return results;
    }

    private static Task<int> CountLinesAsync(string rootPath, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return Task.FromResult(0);
        }

        return Task.FromResult(System.IO.File.ReadAllLines(fullPath).Length);
    }

    private static Task<IReadOnlyList<string>> GetProjectDependenciesAsync(string rootPath, string projectPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, projectPath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var dependencies = new List<string>();
        using var stream = System.IO.File.OpenRead(fullPath);
        var document = XDocument.Load(stream);
        var projectReferences = document.Descendants().Where(element => element.Name.LocalName == "ProjectReference");
        foreach (var reference in projectReferences)
        {
            var include = reference.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                dependencies.Add(include.Replace('\\', '/'));
            }
        }

        var packageReferences = document.Descendants().Where(element => element.Name.LocalName == "PackageReference");
        foreach (var reference in packageReferences)
        {
            var include = reference.Attribute("Include")?.Value;
            var version = reference.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                dependencies.Add(string.IsNullOrWhiteSpace(version) ? include : $"{include}@{version}");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(dependencies);
    }

    private static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        return "private";
    }
}
