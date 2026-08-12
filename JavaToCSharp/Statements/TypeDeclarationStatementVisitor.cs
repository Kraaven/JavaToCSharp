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
        var classSyntax = ClassOrInterfaceDeclarationVisitor.VisitClassDeclaration(context, typeDeclaration);
        return ToStatement(classSyntax);
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
        var classSyntax = RecordDeclarationVisitor.VisitRecordDeclaration(context, recordDeclaration, isNested: true);
        return TypeDeclarationStatementVisitor.ToStatement(classSyntax);
    }
}
