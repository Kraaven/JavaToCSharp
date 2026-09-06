using com.github.javaparser.ast.body;
using com.github.javaparser.ast.stmt;
using JavaToCSharp.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public class TypeDeclarationStatementVisitor : StatementVisitor<LocalClassDeclarationStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, LocalClassDeclarationStmt statement)
    {
        var typeDeclaration = statement.getClassDeclaration();

        // C# has no local classes. Hoist the declaration out of the method body into a private
        // nested type (published via PendingAnonymousTypes) with explicit capture plumbing.
        return LocalClassHoister.HoistLocalType(context, statement, typeDeclaration);
    }

    internal static StatementSyntax? ToStatement(ClassDeclarationSyntax? classSyntax)
    {
        var text = classSyntax?.NormalizeWhitespace().GetText().ToString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return SyntaxFactory.ParseStatement(text);
    }
}

public class LocalRecordDeclarationStatementVisitor : StatementVisitor<LocalRecordDeclarationStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, LocalRecordDeclarationStmt statement)
    {
        var recordDeclaration = statement.getRecordDeclaration();
        return LocalClassHoister.HoistLocalType(context, statement, recordDeclaration);
    }
}
