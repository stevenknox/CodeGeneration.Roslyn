﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MS-PL license. See LICENSE.txt file in the project root for full license information.

namespace CodeGeneration.Roslyn.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Validation;

    /// <summary>
    /// The class responsible for generating compilation units to add to the project being built.
    /// </summary>
    public static class DocumentTransform
    {
        /// <summary>
        /// A "generated by tool" comment string with environment/os-normalized newlines.
        /// </summary>
        public static readonly string GeneratedByAToolPreamble = @"// ------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
// ------------------------------------------------------------------------------
".Replace("\r\n", "\n").Replace("\n", Environment.NewLine); // normalize regardless of git checkout policy

        /// <summary>
        /// Produces a new document in response to any code generation attributes found in the specified document.
        /// </summary>
        /// <param name="compilation">The compilation to which the document belongs.</param>
        /// <param name="inputDocument">The document to scan for generator attributes.</param>
        /// <param name="projectDirectory">The path of the <c>.csproj</c> project file.</param>
        /// <param name="assemblyLoader">A function that can load an assembly with the given name.</param>
        /// <param name="progress">Reports warnings and errors in code generation.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A task whose result is the generated document.</returns>
        public static async Task<SyntaxTree> TransformAsync(
            CSharpCompilation compilation,
            SyntaxTree inputDocument,
            string projectDirectory,
            Func<AssemblyName, Assembly> assemblyLoader,
            IProgress<Diagnostic> progress,
            CancellationToken cancellationToken)
        {
            Requires.NotNull(compilation, nameof(compilation));
            Requires.NotNull(inputDocument, nameof(inputDocument));
            Requires.NotNull(assemblyLoader, nameof(assemblyLoader));

            var inputSemanticModel = compilation.GetSemanticModel(inputDocument);
            var inputCompilationUnit = inputDocument.GetCompilationUnitRoot();

            var emittedExterns = inputCompilationUnit
                .Externs
                .Select(x => x.WithoutTrivia())
                .ToImmutableArray();

            var emittedUsings = inputCompilationUnit
                .Usings
                .Select(x => x.WithoutTrivia())
                .ToImmutableArray();

            var emittedAttributeLists = ImmutableArray<AttributeListSyntax>.Empty;
            var emittedMembers = ImmutableArray<MemberDeclarationSyntax>.Empty;

            var root = await inputDocument.GetRootAsync(cancellationToken);
            var memberNodes = root
                .DescendantNodesAndSelf(n => n is CompilationUnitSyntax || n is NamespaceDeclarationSyntax || n is TypeDeclarationSyntax)
                .OfType<CSharpSyntaxNode>();

            foreach (var memberNode in memberNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributeData = GetAttributeData(compilation, inputSemanticModel, memberNode);
                var generators = FindCodeGenerators(attributeData, assemblyLoader);
                foreach (var generator in generators)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = new TransformationContext(
                        memberNode,
                        inputSemanticModel,
                        compilation,
                        projectDirectory,
                        emittedUsings,
                        emittedExterns);

                    var richGenerator = generator as IRichCodeGenerator ?? new EnrichingCodeGeneratorProxy(generator);

                    var emitted = await richGenerator.GenerateRichAsync(context, progress, cancellationToken);

                    emittedExterns = emittedExterns.AddRange(emitted.Externs);
                    emittedUsings = emittedUsings.AddRange(emitted.Usings);
                    emittedAttributeLists = emittedAttributeLists.AddRange(emitted.AttributeLists);
                    emittedMembers = emittedMembers.AddRange(emitted.Members);
                }
            }

            var compilationUnit =
                SyntaxFactory.CompilationUnit(
                        SyntaxFactory.List(emittedExterns),
                        SyntaxFactory.List(emittedUsings),
                        SyntaxFactory.List(emittedAttributeLists),
                        SyntaxFactory.List(emittedMembers))
                    .WithLeadingTrivia(SyntaxFactory.Comment(GeneratedByAToolPreamble))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                    .NormalizeWhitespace();

            return compilationUnit.SyntaxTree;
        }

        private static ImmutableArray<AttributeData> GetAttributeData(Compilation compilation, SemanticModel document, SyntaxNode syntaxNode)
        {
            Requires.NotNull(compilation, nameof(compilation));
            Requires.NotNull(document, nameof(document));
            Requires.NotNull(syntaxNode, nameof(syntaxNode));

            switch (syntaxNode)
            {
                case CompilationUnitSyntax syntax:
                    return compilation.Assembly.GetAttributes().Where(x => x.ApplicationSyntaxReference.SyntaxTree == syntax.SyntaxTree).ToImmutableArray();
                default:
                    return document.GetDeclaredSymbol(syntaxNode)?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty;
            }
        }

        private static IEnumerable<ICodeGenerator> FindCodeGenerators(ImmutableArray<AttributeData> nodeAttributes, Func<AssemblyName, Assembly> assemblyLoader)
        {
            foreach (var attributeData in nodeAttributes)
            {
                Type generatorType = GetCodeGeneratorTypeForAttribute(attributeData.AttributeClass, assemblyLoader);
                if (generatorType != null)
                {
                    ICodeGenerator generator;
                    try
                    {
                        generator = (ICodeGenerator)Activator.CreateInstance(generatorType, attributeData);
                    }
                    catch (MissingMethodException)
                    {
                        throw new InvalidOperationException(
                            $"Failed to instantiate {generatorType}. ICodeGenerator implementations must have" +
                            $" a constructor accepting Microsoft.CodeAnalysis.AttributeData argument.");
                    }
                    yield return generator;
                }
            }
        }

        private static Type GetCodeGeneratorTypeForAttribute(INamedTypeSymbol attributeType, Func<AssemblyName, Assembly> assemblyLoader)
        {
            Requires.NotNull(assemblyLoader, nameof(assemblyLoader));

            if (attributeType != null)
            {
                foreach (var generatorCandidateAttribute in attributeType.GetAttributes())
                {
                    if (generatorCandidateAttribute.AttributeClass.Name == typeof(CodeGenerationAttributeAttribute).Name)
                    {
                        string assemblyName = null;
                        string fullTypeName = null;
                        TypedConstant firstArg = generatorCandidateAttribute.ConstructorArguments.Single();
                        if (firstArg.Value is string typeName)
                        {
                            // This string is the full name of the type, which MAY be assembly-qualified.
                            int commaIndex = typeName.IndexOf(',');
                            bool isAssemblyQualified = commaIndex >= 0;
                            if (isAssemblyQualified)
                            {
                                fullTypeName = typeName.Substring(0, commaIndex);
                                assemblyName = typeName.Substring(commaIndex + 1).Trim();
                            }
                            else
                            {
                                fullTypeName = typeName;
                                assemblyName = generatorCandidateAttribute.AttributeClass.ContainingAssembly.Name;
                            }
                        }
                        else if (firstArg.Value is INamedTypeSymbol typeOfValue)
                        {
                            // This was a typeof(T) expression
                            fullTypeName = GetFullTypeName(typeOfValue);
                            assemblyName = typeOfValue.ContainingAssembly.Name;
                        }

                        if (assemblyName != null)
                        {
                            var assembly = assemblyLoader(new AssemblyName(assemblyName));
                            if (assembly != null)
                            {
                                return assembly.GetType(fullTypeName);
                            }
                        }

                        Verify.FailOperation("Unable to find code generator: {0} in {1}", fullTypeName, assemblyName);
                    }
                }
            }

            return null;
        }

        private static string GetFullTypeName(INamedTypeSymbol symbol)
        {
            Requires.NotNull(symbol, nameof(symbol));

            var nameBuilder = new StringBuilder();
            ISymbol symbolOrParent = symbol;
            while (symbolOrParent != null && !string.IsNullOrEmpty(symbolOrParent.Name))
            {
                if (nameBuilder.Length > 0)
                {
                    nameBuilder.Insert(0, ".");
                }

                nameBuilder.Insert(0, symbolOrParent.Name);
                symbolOrParent = symbolOrParent.ContainingSymbol;
            }

            return nameBuilder.ToString();
        }

        private class EnrichingCodeGeneratorProxy : IRichCodeGenerator
        {
            public EnrichingCodeGeneratorProxy(ICodeGenerator codeGenerator)
            {
                Requires.NotNull(codeGenerator, nameof(codeGenerator));
                CodeGenerator = codeGenerator;
            }

            private ICodeGenerator CodeGenerator { get; }

            public Task<SyntaxList<MemberDeclarationSyntax>> GenerateAsync(
                TransformationContext context,
                IProgress<Diagnostic> progress,
                CancellationToken cancellationToken)
            {
                return CodeGenerator.GenerateAsync(context, progress, cancellationToken);
            }

            public async Task<RichGenerationResult> GenerateRichAsync(TransformationContext context, IProgress<Diagnostic> progress, CancellationToken cancellationToken)
            {
                var generatedMembers = await CodeGenerator.GenerateAsync(context, progress, cancellationToken);

                // Figure out ancestry for the generated type, including nesting types and namespaces.
                var wrappedMembers = context.ProcessingNode.Ancestors().Aggregate(generatedMembers, WrapInAncestor);
                return new RichGenerationResult { Members = wrappedMembers };
            }

            private static SyntaxList<MemberDeclarationSyntax> WrapInAncestor(SyntaxList<MemberDeclarationSyntax> generatedMembers, SyntaxNode ancestor)
            {
                switch (ancestor)
                {
                    case NamespaceDeclarationSyntax ancestorNamespace:
                        generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                            CopyAsAncestor(ancestorNamespace)
                            .WithMembers(generatedMembers));
                        break;
                    case ClassDeclarationSyntax nestingClass:
                        generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                            CopyAsAncestor(nestingClass)
                            .WithMembers(generatedMembers));
                        break;
                    case StructDeclarationSyntax nestingStruct:
                        generatedMembers = SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                            CopyAsAncestor(nestingStruct)
                            .WithMembers(generatedMembers));
                        break;
                }
                return generatedMembers;
            }

            private static NamespaceDeclarationSyntax CopyAsAncestor(NamespaceDeclarationSyntax syntax)
            {
                return SyntaxFactory.NamespaceDeclaration(syntax.Name.WithoutTrivia())
                    .WithExterns(SyntaxFactory.List(syntax.Externs.Select(x => x.WithoutTrivia())))
                    .WithUsings(SyntaxFactory.List(syntax.Usings.Select(x => x.WithoutTrivia())));
            }

            private static ClassDeclarationSyntax CopyAsAncestor(ClassDeclarationSyntax syntax)
            {
                return SyntaxFactory.ClassDeclaration(syntax.Identifier.WithoutTrivia())
                    .WithModifiers(SyntaxFactory.TokenList(syntax.Modifiers.Select(x => x.WithoutTrivia())))
                    .WithTypeParameterList(syntax.TypeParameterList);
            }

            private static StructDeclarationSyntax CopyAsAncestor(StructDeclarationSyntax syntax)
            {
                return SyntaxFactory.StructDeclaration(syntax.Identifier.WithoutTrivia())
                    .WithModifiers(SyntaxFactory.TokenList(syntax.Modifiers.Select(x => x.WithoutTrivia())))
                    .WithTypeParameterList(syntax.TypeParameterList);
            }
        }
    }
}
