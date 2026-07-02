    public async Task<IReadOnlyList<ReferenceSummary>> FindReferencesAsync(string query, CancellationToken cancellationToken = default)
    {
        var targetSearch = await FindSymbolsContainingAsync(query, cancellationToken);
        if (targetSearch.Count == 0)
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.qualified_name, s.kind, n.name, p.name, f.path, t.name, r.root_path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            ORDER BY s.qualified_name;
            """;
        var candidates = new List<(string Id, string Name, string QualifiedName, string Kind, string Namespace, string Project, string FilePath, string TypeName, string RootPath)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
            }
        }

        var results = new List<ReferenceSummary>();
        var addedIds = new HashSet<string>();
        foreach (var target in targetSearch)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Id == target.Id || !System.IO.File.Exists(Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                // If candidate is the same as the search hit
                var fullPath = Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar));
                var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
                
                // Very basic heuristic for finding references without symbol analysis (similar to the caller behavior)
                if (sourceText.Contains(target.Name, StringComparison.Ordinal) && !addedIds.Contains(candidate.Id))
                {
                    addedIds.Add(candidate.Id);
                    results.Add(new ReferenceSummary(
                        System.Guid.NewGuid().ToString(),
                        candidate.Name,
                        candidate.QualifiedName,
                        candidate.Kind,
                        candidate.Kind == "Method" || candidate.Kind == "Constructor" ? candidate.Name : "",
                        candidate.TypeName,
                        candidate.Namespace,
                        candidate.Project,
                        candidate.FilePath,
                        null
                    ));
                }
            }
        }

        return results;
    }
