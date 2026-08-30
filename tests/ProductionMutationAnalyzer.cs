using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;

namespace VmbLauncher.Tests;

internal static class ProductionMutationAnalyzer
{
    private static readonly HashSet<string> FileReadOnlyMethods = new(StringComparer.Ordinal)
    {
        "Exists", "GetAttributes", "GetCreationTime", "GetCreationTimeUtc", "GetLastAccessTime",
        "GetLastAccessTimeUtc", "GetLastWriteTime", "GetLastWriteTimeUtc", "GetUnixFileMode", "OpenRead",
        "OpenText", "ReadAllBytes", "ReadAllBytesAsync", "ReadAllLines", "ReadAllLinesAsync", "ReadAllText",
        "ReadAllTextAsync", "ReadLines", "ResolveLinkTarget",
    };

    private static readonly HashSet<string> DirectoryReadOnlyMethods = new(StringComparer.Ordinal)
    {
        "EnumerateDirectories", "EnumerateFiles", "EnumerateFileSystemEntries", "Exists", "GetCreationTime",
        "GetCreationTimeUtc", "GetCurrentDirectory", "GetDirectories", "GetDirectoryRoot", "GetFiles",
        "GetFileSystemEntries", "GetLastAccessTime", "GetLastAccessTimeUtc", "GetLastWriteTime",
        "GetLastWriteTimeUtc", "GetLogicalDrives", "GetParent", "GetUnixFileMode", "ResolveLinkTarget",
    };

    private static readonly HashSet<string> FileInfoReadOnlyMethods = new(StringComparer.Ordinal)
    {
        "Equals", "GetAccessControl", "GetHashCode", "OpenRead", "OpenText", "Refresh", "ResolveLinkTarget",
        "ToString",
    };

    private static readonly HashSet<string> DirectoryInfoReadOnlyMethods = new(StringComparer.Ordinal)
    {
        "EnumerateDirectories", "EnumerateFiles", "EnumerateFileSystemInfos", "Equals", "GetAccessControl",
        "GetDirectories", "GetFiles", "GetFileSystemInfos", "GetHashCode", "Refresh", "ResolveLinkTarget",
        "ToString",
    };

    private static readonly HashSet<string> FileSystemInfoWritableProperties = new(StringComparer.Ordinal)
    {
        "Attributes", "CreationTime", "CreationTimeUtc", "IsReadOnly", "LastAccessTime", "LastAccessTimeUtc",
        "LastWriteTime", "LastWriteTimeUtc", "UnixFileMode",
    };

    private static readonly HashSet<string> NativeMutationCalls = new(StringComparer.Ordinal)
    {
        "AssignProcessToJobObject", "CreateJobObjectW", "CreateProcessW", "SetInformationJobObject",
        "SetKernelObjectSecurity", "NtCreateFile", "NtSetInformationFile", "SetFileInformationByHandle",
        "SetFileInformationByHandleBuffer",
    };

    private static readonly HashSet<string> StreamMutationMethods = new(StringComparer.Ordinal)
    {
        "BeginWrite", "CopyTo", "CopyToAsync", "EndWrite", "Flush", "FlushAsync", "SetLength", "Write",
        "WriteAsync", "WriteByte", "WriteLine", "WriteLineAsync",
    };

    private static readonly HashSet<string> SensitiveNames = new(
        FileSystemInfoWritableProperties
            .Concat(NativeMutationCalls)
            .Concat(StreamMutationMethods)
            .Concat(new[]
            {
                "AppendAllLines", "AppendAllLinesAsync", "AppendAllText", "AppendAllTextAsync", "AppendText",
                "Copy", "CopyTo", "Create", "CreateAsSymbolicLink", "CreateDirectory", "CreateSubdirectory",
                "CreateSymbolicLink", "CreateText", "Decrypt", "Delete", "Encrypt", "Move", "MoveTo", "Open",
                "OpenHandle", "OpenWrite", "Replace", "SetAccessControl", "SetAttributes", "SetCreationTime",
                "SetCreationTimeUtc", "SetCurrentDirectory", "SetLastAccessTime", "SetLastAccessTimeUtc",
                "SetLastWriteTime", "SetLastWriteTimeUtc", "SetUnixFileMode", "Start", "ExtractToDirectory",
                "Kill", "WriteAllBytes", "WriteAllBytesAsync", "WriteAllLines", "WriteAllLinesAsync",
                "WriteAllText", "WriteAllTextAsync",
            }),
        StringComparer.Ordinal);

    internal static HashSet<string> Analyze(IReadOnlyDictionary<string, string> sources) =>
        AnalyzeConfigurations(sources, allowedDiagnostics: null);

    internal static HashSet<string> AnalyzeProduction(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, string> semanticSupportSources,
        IReadOnlySet<string> allowedDiagnostics) =>
        AnalyzeConfigurations(sources, allowedDiagnostics, semanticSupportSources);

    private static HashSet<string> AnalyzeConfigurations(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlySet<string>? allowedDiagnostics,
        IReadOnlyDictionary<string, string>? semanticSupportSources = null)
    {
        var sites = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in new[]
                 {
                     (Name: "debug", Symbols: new[] { "DEBUG", "TRACE" }),
                     (Name: "release", Symbols: Array.Empty<string>()),
                 })
        {
            var parseOptions = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Preview)
                .WithPreprocessorSymbols(configuration.Symbols);
            var trees = sources
                .Select(pair => CSharpSyntaxTree.ParseText(pair.Value, parseOptions, pair.Key))
                .ToArray();
            var supportTrees = (semanticSupportSources ?? new Dictionary<string, string>())
                .Select(pair => CSharpSyntaxTree.ParseText(pair.Value, parseOptions, pair.Key))
                .ToArray();
            var implicitUsings = CSharpSyntaxTree.ParseText("""
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;
                """, parseOptions, "__ImplicitUsings.g.cs");
            var compilation = CSharpCompilation.Create(
                $"MutationCensus_{configuration.Name}",
                trees.Concat(supportTrees).Append(implicitUsings),
                PlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            foreach (var diagnostic in compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error))
                diagnostics.Add(FormatDiagnostic(configuration.Name, diagnostic));

            foreach (var tree in trees)
            {
                var walker = new MutationWalker(compilation.GetSemanticModel(tree, ignoreAccessibility: true));
                walker.Visit(tree.GetRoot());
                var occurrences = new Dictionary<(int Line, string Kind), int>();
                foreach (var match in walker.Matches.OrderBy(item => item.Node.SpanStart)
                             .ThenBy(item => item.Kind, StringComparer.Ordinal))
                {
                    var position = tree.GetLineSpan(match.Node.Span).StartLinePosition;
                    var line = position.Line + 1;
                    var column = position.Character + 1;
                    var occurrenceKey = (line, match.Kind);
                    occurrences.TryGetValue(occurrenceKey, out var occurrence);
                    occurrences[occurrenceKey] = ++occurrence;
                    sites.Add($"{tree.FilePath}:{line}:{column}:{occurrence}:{match.Kind}");
                }
            }
        }

        if (allowedDiagnostics is not null && !diagnostics.SetEquals(allowedDiagnostics))
            throw new InvalidOperationException(
                "Production semantic compilation diagnostics drifted.\n" +
                "Unreviewed:\n" + string.Join("\n", diagnostics.Except(allowedDiagnostics).Order()) + "\n" +
                "Missing/moved:\n" + string.Join("\n", allowedDiagnostics.Except(diagnostics).Order()));
        return sites;
    }

    private static string FormatDiagnostic(string configuration, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var path = string.IsNullOrEmpty(span.Path) ? "<global>" : span.Path.Replace('\\', '/');
        var line = span.StartLinePosition.Line + 1;
        var column = span.StartLinePosition.Character + 1;
        return $"{configuration}:{path}:{line}:{column}:{diagnostic.Id}:{diagnostic.GetMessage()}";
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private sealed class MutationWalker(SemanticModel model) : CSharpSyntaxWalker
    {
        internal List<(SyntaxNode Node, string Kind)> Matches { get; } = new();

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var symbolInfo = model.GetSymbolInfo(node);
            var method = symbolInfo.Symbol as IMethodSymbol;
            var candidateKinds = method is null
                ? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>()
                    .Select(candidate => ClassifyInvocation(node, candidate))
                    .Where(kind => kind is not null)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string?>();
            var kind = method is not null
                ? ClassifyInvocation(node, method)
                : candidateKinds.Length == 1
                    ? candidateKinds[0]
                    : ClassifyUnresolvedInvocation(node);
            if (kind is not null) Matches.Add((node, kind));
            base.VisitInvocationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            ClassifyMethodGroup(node);
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            // Covers statically imported method groups (for example,
            // `Action<string> remove = Delete`). Member-access names are
            // classified by their containing MemberAccessExpression so a
            // qualified method group is never counted twice.
            if (node.Parent is not MemberAccessExpressionSyntax)
                ClassifyMethodGroup(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            ClassifyCreation(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            ClassifyCreation(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            ClassifyPropertyWrite(node.Left, node);
            base.VisitAssignmentExpression(node);
        }

        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.PreIncrementExpression) ||
                node.IsKind(SyntaxKind.PreDecrementExpression))
                ClassifyPropertyWrite(node.Operand, node);
            base.VisitPrefixUnaryExpression(node);
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.PostIncrementExpression) ||
                node.IsKind(SyntaxKind.PostDecrementExpression))
                ClassifyPropertyWrite(node.Operand, node);
            base.VisitPostfixUnaryExpression(node);
        }

        private void ClassifyCreation(BaseObjectCreationExpressionSyntax node)
        {
            var type = model.GetTypeInfo(node).Type;
            if (DerivesFrom(type, "System.IO.FileStream"))
                Matches.Add((node, "new FileStream"));
            else if (DerivesFrom(type, "System.IO.StreamWriter"))
                Matches.Add((node, "new StreamWriter"));
        }

        private void ClassifyPropertyWrite(ExpressionSyntax expression, SyntaxNode site)
        {
            if (model.GetSymbolInfo(expression).Symbol is IPropertySymbol property &&
                FileSystemInfoWritableProperties.Contains(property.Name) &&
                DerivesFrom(property.ContainingType, "System.IO.FileSystemInfo"))
                Matches.Add((site, "FileSystemInfo property write"));
        }

        private void ClassifyMethodGroup(ExpressionSyntax node)
        {
            if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
                return;
            if (model.GetTypeInfo(node).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                return;
            if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method)
                return;

            var receiverType = node is MemberAccessExpressionSyntax member
                ? model.GetTypeInfo(member.Expression).Type
                : method.ReceiverType;
            var kind = ClassifyMethod(method, receiverType, isConsoleWriter: false);
            if (kind is not null) Matches.Add((node, kind));
        }

        private string? ClassifyInvocation(InvocationExpressionSyntax node, IMethodSymbol invokedMethod)
        {
            var method = invokedMethod.ReducedFrom ?? invokedMethod;
            var receiverType = ReceiverType(node) ?? invokedMethod.ReceiverType ?? invokedMethod.ContainingType;
            var nativeCreate = ClassifyNativeCreate(node, method);
            if (nativeCreate is not null) return nativeCreate;
            return ClassifyMethod(method, receiverType, IsConsoleWriterReceiver(node));
        }

        private string? ClassifyNativeCreate(
            InvocationExpressionSyntax node,
            IMethodSymbol method)
        {
            if (method.Name is not ("CreateFileW" or "CreateFileForDeleteW")) return null;
            var disposition = method.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, "creationDisposition", StringComparison.OrdinalIgnoreCase));
            if (disposition == null) return "CreateFileW(create-or-unresolved)";
            var argument = node.ArgumentList.Arguments.FirstOrDefault(item =>
                item.NameColon?.Name.Identifier.ValueText == disposition.Name) ??
                (disposition.Ordinal < node.ArgumentList.Arguments.Count
                    ? node.ArgumentList.Arguments[disposition.Ordinal]
                    : null);
            if (argument == null) return "CreateFileW(create-or-unresolved)";
            var constant = model.GetConstantValue(argument.Expression);
            if (!constant.HasValue) return "CreateFileW(create-or-unresolved)";
            try
            {
                // OPEN_EXISTING (3) is the only non-creating disposition used
                // by the exact-set lane. Every other constant may create,
                // truncate, or replace a filesystem object.
                return Convert.ToUInt32(constant.Value) == 3
                    ? null
                    : "CreateFileW(create)";
            }
            catch (Exception)
            {
                return "CreateFileW(create-or-unresolved)";
            }
        }

        private static string? ClassifyMethod(
            IMethodSymbol method, ITypeSymbol? receiverType, bool isConsoleWriter)
        {
            var containingType = FullName(method.ContainingType);
            if (NativeMutationCalls.Contains(method.Name)) return method.Name;

            if (containingType == "System.Diagnostics.Process")
            {
                if (method.Name == "Start") return method.IsStatic ? "Process.Start" : "Process.instance.Start";
                if (method.Name == "Kill") return "Process.instance.Kill";
            }

            if (containingType == "System.IO.File")
                return FileReadOnlyMethods.Contains(method.Name) ? null : "File API";
            if (containingType == "System.IO.Directory")
                return DirectoryReadOnlyMethods.Contains(method.Name) ? null : "Directory API";
            if (containingType == "System.IO.RandomAccess" &&
                method.Name is "Write" or "WriteAsync")
                return "RandomAccess.Write";
            if (containingType is "System.IO.Compression.ZipFile" or "System.IO.Compression.ZipFileExtensions" &&
                method.Name == "ExtractToDirectory")
                return "ZipFile.ExtractToDirectory";
            if (containingType == "System.IO.FileSystemAclExtensions" && method.Name == "SetAccessControl")
                return "FileSystemAclExtensions.SetAccessControl";

            if (DerivesFrom(receiverType, "System.IO.FileInfo"))
                return FileInfoReadOnlyMethods.Contains(method.Name) ? null : "FileInfo instance API";
            if (DerivesFrom(receiverType, "System.IO.DirectoryInfo"))
                return DirectoryInfoReadOnlyMethods.Contains(method.Name) ? null : "DirectoryInfo instance API";
            if (DerivesFrom(receiverType, "System.IO.FileSystemInfo"))
                return method.Name is "Refresh" or "ResolveLinkTarget" or "ToString" or "Equals" or "GetHashCode"
                    ? null
                    : "FileSystemInfo instance API";

            if (StreamMutationMethods.Contains(method.Name) &&
                (DerivesFrom(receiverType, "System.IO.Stream") ||
                 (DerivesFrom(receiverType, "System.IO.TextWriter") && !isConsoleWriter) ||
                 DerivesFrom(receiverType, "System.IO.BinaryWriter")))
                return method.Name.StartsWith("CopyTo", StringComparison.Ordinal) ? "stream.CopyTo" :
                    method.Name == "SetLength" ? "stream.SetLength" :
                    method.Name.StartsWith("Flush", StringComparison.Ordinal) ? "stream.Flush" : "stream.Write";

            return null;
        }

        private bool IsConsoleWriterReceiver(InvocationExpressionSyntax node)
        {
            if (node.Expression is not MemberAccessExpressionSyntax invocationMember ||
                invocationMember.Expression is not MemberAccessExpressionSyntax receiverMember)
                return false;
            return model.GetSymbolInfo(receiverMember).Symbol is IPropertySymbol property &&
                   FullName(property.ContainingType) == "System.Console" &&
                   property.Name is "Out" or "Error";
        }

        private ITypeSymbol? ReceiverType(InvocationExpressionSyntax node) => node.Expression switch
        {
            MemberAccessExpressionSyntax member => model.GetTypeInfo(member.Expression).Type,
            MemberBindingExpressionSyntax => model.GetTypeInfo(node.Expression).Type,
            _ => null,
        };

        private static string? ClassifyUnresolvedInvocation(InvocationExpressionSyntax node)
        {
            var name = node.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                _ => null,
            };
            return name is not null && SensitiveNames.Contains(name) ? "UNRESOLVED mutation candidate" : null;
        }
    }

    private static bool DerivesFrom(ITypeSymbol? type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (FullName(current) == metadataName) return true;
        return false;
    }

    private static string FullName(ISymbol? symbol) =>
        symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal)
        ?? string.Empty;
}
