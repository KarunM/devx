using System.Text.RegularExpressions;
using DevX.Domain;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed class PatchLifecycleService
{
    public PatchPreviewResponse Preview(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        var diffPreview = string.Join("\n", patch.Operations.Select(BuildPreview));
        return new PatchPreviewResponse(validation.Errors.Count == 0, validation.Summary with { LifecycleStage = "preview" }, diffPreview, validation.Errors);
    }

    public PatchValidateResponse ValidateDocument(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        var warnings = validation.Errors.Where(error => error.Code == "Warning").Select(error => error.Message).ToArray();
        var errors = validation.Errors.Where(error => error.Code != "Warning").Select(error => error.Message).ToArray();
        return new PatchValidateResponse(errors.Length == 0, validation.Summary with { LifecycleStage = "validate" }, warnings, errors);
    }

    public (PatchApplyResponse? Response, ProtocolError? Error) Apply(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        if (validation.Errors.Count > 0)
        {
            var first = validation.Errors[0];
            return (null, new ProtocolError(first.Code, first.Message));
        }

        foreach (var operation in patch.Operations)
        {
            var result = ApplyOperation(repositoryRoot, operation);
            if (!result.Success)
            {
                return (null, new ProtocolError(result.Code, result.Message));
            }
        }

        return (new PatchApplyResponse(true, validation.Summary with { LifecycleStage = "apply" }, validation.Errors), null);
    }

    public (PatchRollbackResponse? Response, ProtocolError? Error) Rollback(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        var unsupported = validation.Summary.Contracts.Where(contract => !contract.RollbackSupported).ToArray();
        if (unsupported.Length > 0)
        {
            var first = unsupported[0];
            return (null, new ProtocolError("ValidationError", $"Rollback does not support operation '{first.Operation}' on '{first.Path}'. {first.RollbackReason}".Trim()));
        }

        var restoredFiles = 0;
        var revertedOperations = 0;

        foreach (var operation in patch.Operations.Reverse())
        {
            var absolutePath = Path.GetFullPath(Path.Combine(repositoryRoot, operation.Path));
            if (!absolutePath.StartsWith(Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase))
            {
                return (null, new ProtocolError("ValidationError", $"Path '{operation.Path}' is outside the repository root."));
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase))
            {
                if (!IOFile.Exists(absolutePath))
                {
                    return (null, new ProtocolError("NotFound", $"File '{operation.Path}' was not found for rollback."));
                }

                IOFile.Delete(absolutePath);
                restoredFiles++;
                revertedOperations++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(operation.BeforeState))
            {
                return (null, new ProtocolError("ValidationError", $"Rollback requires before-state metadata for operation '{operation.Operation}'."));
            }

            IOFile.WriteAllText(absolutePath, operation.BeforeState);
            restoredFiles++;
            revertedOperations++;
        }

        return (new PatchRollbackResponse(true, validation.Summary with { LifecycleStage = "rollback" }, restoredFiles, revertedOperations, [], []), null);
    }

    private static PatchValidationResult Validate(string repositoryRoot, PatchDocument patch)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var errors = new List<ProtocolDiagnostic>();
        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contracts = new List<PatchOperationContract>();

        foreach (var operation in patch.Operations)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
            var rollback = GetRollbackContract(operation);
            contracts.Add(new PatchOperationContract(operation.Operation, operation.Path, rollback.RollbackSupported, rollback.RollbackAvailability, rollback.RollbackReason));

            var operationKey = $"{operation.Operation}:{operation.Path}";
            if (!seenOperations.Add(operationKey))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Duplicate operation detected for '{operation.Operation}' on '{operation.Path}'."));
            }

            if (!absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Path '{operation.Path}' is outside the repository root."));
                continue;
            }

            if (IsUnsafeUpdateFile(operation, absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "Whole-file replacement is not permitted for source files."));
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' already exists."));
            }

            if (!operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && !IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' does not exist."));
            }

            if (IsTextReplace(operation.Operation) && string.IsNullOrEmpty(operation.OldText))
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceText requires oldText."));
            if (IsTextReplace(operation.Operation) && operation.ReplacementText is null)
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceText requires replacementText."));
            if (IsTextReplace(operation.Operation) && IOFile.Exists(absolutePath) && !string.IsNullOrEmpty(operation.OldText))
            {
                var matches = CountOccurrences(IOFile.ReadAllText(absolutePath), operation.OldText);
                if (matches == 0) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText old text was not found in [{operation.Path}] (0 matches)."));
                else if (!operation.ReplaceAll && operation.Occurrence is null && matches > 1) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText is ambiguous in [{operation.Path}] ({matches} matches); specify occurrence or ReplaceAll."));
                else if (operation.Occurrence is int occurrence && (occurrence < 1 || occurrence > matches)) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText occurrence {occurrence} is out of range ({matches} matches)."));
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.ReplacementText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceMethod requires replacementText."));
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(operation.MethodName) && IOFile.Exists(absolutePath))
            {
                var replaceMethodMatches = StructuredSymbolReader.CanParse(absolutePath)
                    ? FindStructuredMethods(absolutePath, operation.MethodName).Count
                    : Regex.Matches(IOFile.ReadAllText(absolutePath), $@"(?m)^[ \t]*(?:public|private|protected|internal|static|async|sealed|override|partial|virtual|extern|unsafe|new|readonly|ref)[^\n]*\b{Regex.Escape(operation.MethodName)}\b[^\n]*$").Count;
                if (replaceMethodMatches == 0)
                {
                    errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceMethod target [{operation.MethodName}] was not found in [{operation.Path}]."));
                }
                else if (replaceMethodMatches > 1)
                {
                    errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceMethod target [{operation.MethodName}] is ambiguous in [{operation.Path}] ({replaceMethodMatches} matches)."));
                }
            }

            if (operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.MethodText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "InsertMethod requires methodText."));
            }

            if (operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.Namespace))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "AddUsing requires namespace."));
            }

            if (operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase) && StructuredSymbolReader.CanParse(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("UnsupportedLanguage", "AddUsing is C#-only; use an explicit import/text patch for Python or Markdown."));
            }

            if (operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "UpdateFile remains blocked for source files."));
            }
        }

        var filesAffected = patch.Operations.Select(operation => operation.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var operationClasses = patch.Operations.Select(operation => operation.Operation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rollbackSupported = contracts.All(contract => contract.RollbackSupported);
        var summary = new PatchLifecycleSummary(
            "validate",
            "patch",
            patch.Operations.Count,
            filesAffected.Length,
            filesAffected,
            operationClasses,
            rollbackSupported,
            contracts);

        return new PatchValidationResult(summary, errors);
    }

    private static PatchOperationRollbackContract GetRollbackContract(PatchOperation operation)
    {
        if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase))
        {
            return new PatchOperationRollbackContract(true, "Supported", null);
        }

        if (IsTextReplace(operation.Operation) || operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase)
            || operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase)
            || operation.Operation.Equals("ReplaceLineRange", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(operation.BeforeState)
                ? new PatchOperationRollbackContract(false, "Unavailable", "Before-state metadata is required.")
                : new PatchOperationRollbackContract(true, "Supported", null);
        }

        return operation.Operation.Equals("DeleteFile", StringComparison.OrdinalIgnoreCase)
            ? new PatchOperationRollbackContract(false, "Unavailable", "DeleteFile rollback requires backup content.")
            : new PatchOperationRollbackContract(false, "Unavailable", $"Rollback is not implemented for operation '{operation.Operation}'.");
    }

    private static (bool Success, string Code, string Message) ApplyOperation(string root, PatchOperation operation)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Patch targets must remain inside the repository root.");
        }

        if (IsUnsafeUpdateFile(operation, targetPath))
        {
            return (false, "UnsafeOperation", "Whole-file replacement is not permitted for source files.");
        }

        return operation.Operation switch
        {
            "CreateFile" => ApplyCreateFile(targetPath, operation.Content),
            "DeleteFile" => ApplyDeleteFile(targetPath),
            "ReplaceMethod" => ApplyReplaceMethod(targetPath, operation.MethodName, operation.ReplacementText),
            "ReplaceText" or "TextReplace" => ApplyTextReplace(targetPath, operation.OldText, operation.ReplacementText, operation.Occurrence, operation.ReplaceAll),
            "InsertMethod" => ApplyInsertMethod(targetPath, operation.ContainingType, operation.TypeId, operation.MethodText, operation.Position),
            "AddUsing" => ApplyAddUsing(targetPath, operation.Namespace),
            "InsertText" => ApplyInsertText(targetPath, operation.Content, operation.Position),
            "ReplaceLineRange" => ApplyReplaceLineRange(targetPath, operation.Content, operation.Position),
            _ => (false, "InvalidOperation", $"Unsupported patch operation '{operation.Operation}'.")
        };
    }

    private static bool IsTextReplace(string operation) => operation.Equals("ReplaceText", StringComparison.OrdinalIgnoreCase) || operation.Equals("TextReplace", StringComparison.OrdinalIgnoreCase);
    private static int CountOccurrences(string text, string value)
    {
        var count = 0; var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0) { count++; offset += value.Length; }
        return count;
    }
    private static (bool Success, string Code, string Message) ApplyTextReplace(string targetPath, string? oldText, string? replacement, int? occurrence, bool replaceAll)
    {
        if (!IOFile.Exists(targetPath)) return (false, "NotFound", "Target file does not exist.");
        if (string.IsNullOrEmpty(oldText) || replacement is null) return (false, "ValidationError", "ReplaceText requires oldText and replacementText.");
        var text = IOFile.ReadAllText(targetPath); var count = CountOccurrences(text, oldText);
        if (count == 0) return (false, "ValidationError", "ReplaceText old text was not found (0 matches).");
        if (!replaceAll && occurrence is null && count > 1) return (false, "ValidationError", $"ReplaceText is ambiguous ({count} matches); specify occurrence or ReplaceAll.");
        if (occurrence is int index)
        {
            if (index < 1 || index > count) return (false, "ValidationError", $"ReplaceText occurrence {index} is out of range ({count} matches).");
            var start = -1; var from = 0;
            for (var i = 0; i < index; i++) { start = text.IndexOf(oldText, from, StringComparison.Ordinal); from = start + oldText.Length; }
            text = text[..start] + replacement + text[(start + oldText.Length)..];
        }
        else text = replaceAll ? text.Replace(oldText, replacement, StringComparison.Ordinal) : text.Replace(oldText, replacement, StringComparison.Ordinal);
        IOFile.WriteAllText(targetPath, text); return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyCreateFile(string targetPath, string? content)
    {
        if (IOFile.Exists(targetPath))
        {
            return (false, "ValidationError", "Target file already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        IOFile.WriteAllText(targetPath, content ?? string.Empty);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyDeleteFile(string targetPath)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        IOFile.Delete(targetPath);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceMethod(string targetPath, string? methodName, string? replacementText)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(replacementText))
        {
            return (false, "ValidationError", "ReplaceMethod requires methodName and replacementText.");
        }

        var text = IOFile.ReadAllText(targetPath);
        if (StructuredSymbolReader.CanParse(targetPath))
        {
            var methods = FindStructuredMethods(targetPath, methodName);
            if (methods.Count != 1)
            {
                return (false, "ValidationError", methods.Count == 0 ? "Target method was not found." : "Target method must be unambiguous.");
            }

            IOFile.WriteAllText(targetPath, ReplaceLineSpan(text, methods[0].StartLine, methods[0].EndLine, replacementText));
            return (true, string.Empty, string.Empty);
        }

        var methodPattern = $@"(?m)^[ \t]*(?:public|private|protected|internal|static|async|sealed|override|partial|virtual|extern|unsafe|new|readonly|ref|internal\s+static|private\s+static|public\s+static|protected\s+static)[^\n]*\b{Regex.Escape(methodName)}\b[^\n]*$";
        var signatureMatches = Regex.Matches(text, methodPattern);
        if (signatureMatches.Count != 1)
        {
            return (false, "ValidationError", "Target method must be unambiguous.");
        }

        var signatureMatch = signatureMatches[0];
        var bodyStart = text.IndexOf('{', signatureMatch.Index + signatureMatch.Length);
        if (bodyStart < 0)
        {
            return (false, "ValidationError", "Target method body was not found.");
        }

        var bodyEnd = FindMatchingBrace(text, bodyStart);
        if (bodyEnd < 0)
        {
            return (false, "ValidationError", "Target method body was not balanced.");
        }

        var matchStart = signatureMatch.Index;
        var matchLength = bodyEnd + 1 - matchStart;
        var updated = text[..matchStart] + replacementText + text[(matchStart + matchLength)..];
        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var index = openBraceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static (bool Success, string Code, string Message) ApplyInsertMethod(string targetPath, string? containingType, string? typeId, string? methodText, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodText) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertMethod requires containingType or typeId, methodText, and position.");
        }

        var text = IOFile.ReadAllText(targetPath);
        if (MarkdownSymbolReader.CanParse(targetPath))
        {
            // Markdown has no indentation contract the way Python does: a section is inserted
            // verbatim on its own lines, anchored on a sibling heading's span.
            return InsertMarkdownSection(targetPath, text, containingType, methodText!, position!);
        }

        if (StructuredSymbolReader.CanParse(targetPath))
        {
            var parsed = StructuredSymbolReader.Parse(targetPath);
            var type = parsed.Types.FirstOrDefault(item => string.IsNullOrWhiteSpace(containingType)
                || string.Equals(item.Name, containingType, StringComparison.Ordinal));
            if (type is null)
            {
                return (false, "ValidationError", "Target type or section must exist and be unambiguous.");
            }

            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var anchorName = position[(position.IndexOf(':') + 1)..];
            var pyAnchor = type.Methods.FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal));
            var isClass = !string.Equals(type.Name, "Module", StringComparison.Ordinal);
            var indent = isClass && pyAnchor is not null
                ? new string(lines[pyAnchor.StartLine - 1].TakeWhile(char.IsWhiteSpace).ToArray())
                : isClass ? "    " : string.Empty;
            var inserted = NormalizePythonIndent(methodText, indent).Split('\n');
            var pyInsertAt = pyAnchor is null
                ? (isClass ? type.EndLine - 1 : lines.Count)
                : position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase) ? pyAnchor.StartLine - 1 : pyAnchor.EndLine;
            lines.InsertRange(Math.Clamp(pyInsertAt, 0, lines.Count), inserted);
            IOFile.WriteAllText(targetPath, string.Join("\n", lines));
            return (true, string.Empty, string.Empty);
        }

        var typeMatch = !string.IsNullOrWhiteSpace(containingType)
            ? Regex.Match(text, $@"class\s+{Regex.Escape(containingType)}[\s\S]*?\{{")
            : typeId is null ? Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{") : Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{");

        if (!typeMatch.Success)
        {
            return (false, "ValidationError", "Target type must exist and be unambiguous.");
        }

        var anchor = position[(position.IndexOf(':') + 1)..];
        var insertAt = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase) || position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
            ? text.IndexOf(anchor, StringComparison.Ordinal)
            : text.LastIndexOf('}');

        if (insertAt < 0)
        {
            return (false, "ValidationError", "Insertion point was not found.");
        }

        var updated = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase)
            ? text[..insertAt] + methodText + Environment.NewLine + text[insertAt..]
            : position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
                ? text[..(insertAt + anchor.Length)] + Environment.NewLine + methodText + text[(insertAt + anchor.Length)..]
                : text[..insertAt] + Environment.NewLine + methodText + Environment.NewLine + text[insertAt..];

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    /// <summary>
    /// Inserts a Markdown section relative to an existing heading. `position` is
    /// Before:&lt;heading&gt; / After:&lt;heading&gt;; with no resolvable anchor the section is appended to
    /// the end of the containing section, or the end of the document when none is named.
    /// </summary>
    private static (bool Ok, string Code, string Message) InsertMarkdownSection(
        string targetPath,
        string text,
        string? containingType,
        string sectionText,
        string position)
    {
        var parsed = MarkdownSymbolReader.Parse(targetPath, text);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var section = string.IsNullOrWhiteSpace(containingType)
            ? null
            : parsed.Types.FirstOrDefault(item => string.Equals(item.Name, containingType, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(containingType) && section is null)
        {
            return (false, "ValidationError", "Target type or section must exist and be unambiguous.");
        }

        var separatorIndex = position.IndexOf(':');
        var anchorName = separatorIndex >= 0 ? position[(separatorIndex + 1)..].Trim() : string.Empty;
        var before = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase);

        var anchor = string.IsNullOrWhiteSpace(anchorName)
            ? null
            : (section?.Methods ?? parsed.Types.SelectMany(type => type.Methods).ToArray())
                .FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal))
              ?? (ParsedMethod?)null;

        var anchorType = anchor is null && !string.IsNullOrWhiteSpace(anchorName)
            ? parsed.Types.FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal))
            : null;

        int insertAt;
        if (anchor is not null)
        {
            insertAt = before ? anchor.StartLine - 1 : anchor.EndLine;
        }
        else if (anchorType is not null)
        {
            insertAt = before ? anchorType.StartLine - 1 : anchorType.EndLine;
        }
        else if (section is not null)
        {
            insertAt = section.EndLine;
        }
        else
        {
            insertAt = lines.Count;
        }

        insertAt = Math.Clamp(insertAt, 0, lines.Count);

        var inserted = new List<string>();
        if (insertAt > 0 && !string.IsNullOrWhiteSpace(lines[insertAt - 1]))
        {
            inserted.Add(string.Empty);
        }

        inserted.AddRange(sectionText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));

        if (insertAt < lines.Count && !string.IsNullOrWhiteSpace(lines[insertAt]))
        {
            inserted.Add(string.Empty);
        }

        lines.InsertRange(insertAt, inserted);
        IOFile.WriteAllText(targetPath, string.Join("\n", lines));
        return (true, string.Empty, string.Empty);
    }

    private static IReadOnlyList<ParsedMethod> FindStructuredMethods(string targetPath, string methodName)
        => StructuredSymbolReader.Parse(targetPath).Types
            .SelectMany(type => type.Methods)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();

    private static string ReplaceLineSpan(string text, int startLine, int endLine, string replacement)
    {
        var hadTrailingNewline = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, replacement.Replace("\r\n", "\n").Split('\n'));
        var updated = string.Join("\n", lines);
        return hadTrailingNewline && !updated.EndsWith('\n') ? updated + "\n" : updated;
    }

    private static string NormalizePythonIndent(string methodText, string indent)
    {
        var lines = methodText.Replace("\r\n", "\n").Split('\n');
        var nonEmpty = lines.Where(line => line.Trim().Length > 0).ToArray();
        var baseIndent = nonEmpty.Length == 0 ? 0 : nonEmpty.Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        return string.Join('\n', lines.Select(line => line.Trim().Length == 0 ? string.Empty : indent + line[Math.Min(baseIndent, line.Length)..]));
    }

    private static (bool Success, string Code, string Message) ApplyAddUsing(string targetPath, string? @namespace)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return (false, "ValidationError", "AddUsing requires namespace.");
        }

        var text = IOFile.ReadAllText(targetPath);
        var usingLine = $"using {@namespace};";
        if (text.Contains(usingLine, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Duplicate using is not allowed.");
        }

        var insertAt = text.StartsWith("using ", StringComparison.Ordinal) ? text.LastIndexOf("using ", StringComparison.OrdinalIgnoreCase) : 0;
        var updated = insertAt <= 0
            ? usingLine + Environment.NewLine + text
            : text.Insert(insertAt, usingLine + Environment.NewLine);

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyInsertText(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertText requires Content and Position (e.g., 'after:42').");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        var match = Regex.Match(position, @"(after|before):(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var lineNumber) || lineNumber < 1 || lineNumber > lines.Length)
        {
            return (false, "ValidationError", "Position must be 'after:N' or 'before:N' where N is a valid line number.");
        }

        var insertIndex = match.Groups[1].Value.Equals("after", StringComparison.OrdinalIgnoreCase) ? lineNumber : lineNumber - 1;
        var contentLines = content.Split('\n');
        var newLines = lines.Take(insertIndex).Concat(contentLines).Concat(lines.Skip(insertIndex)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceLineRange(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "ReplaceLineRange requires Content and Position (e.g., '10:15').");
        }

        var rangeMatch = Regex.Match(position, @"(\d+):(\d+)");
        if (!rangeMatch.Success || !int.TryParse(rangeMatch.Groups[1].Value, out var startLine) || !int.TryParse(rangeMatch.Groups[2].Value, out var endLine))
        {
            return (false, "ValidationError", "Position must be 'start:end' line range.");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        if (startLine < 1 || endLine > lines.Length || startLine > endLine)
        {
            return (false, "ValidationError", "Invalid line range.");
        }

        var contentLines = content.Split('\n');
        var newLines = lines.Take(startLine - 1).Concat(contentLines).Concat(lines.Skip(endLine)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static string BuildPreview(PatchOperation operation)
        => operation.Operation switch
        {
            "CreateFile" => $"--- /dev/null\n+++ {operation.Path}\n@@\n+{operation.Content}",
            "DeleteFile" => $"--- {operation.Path}\n+++ /dev/null\n@@",
            "ReplaceText" or "TextReplace" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n-{operation.OldText}\n+{operation.ReplacementText}",
            "ReplaceMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n-{operation.MethodName}\n+{operation.ReplacementText}",
            "InsertMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+{operation.MethodText}",
            "AddUsing" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+using {operation.Namespace};",
            _ => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+unsupported operation {operation.Operation}"
        };

    private static bool IsUnsafeUpdateFile(PatchOperation operation, string targetPath)
        => operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase)
            && (targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));

    private sealed record PatchValidationResult(PatchLifecycleSummary Summary, IReadOnlyList<ProtocolDiagnostic> Errors);
    private sealed record PatchOperationRollbackContract(bool RollbackSupported, string RollbackAvailability, string? RollbackReason);
}
