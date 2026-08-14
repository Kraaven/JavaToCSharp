using com.github.javaparser.ast.stmt;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public class YieldStatementVisitor : StatementVisitor<YieldStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, YieldStmt yieldStmt)
    {
        var statements = SwitchConversion.ConvertYield(context, yieldStmt).ToList();

        return statements.Count switch
        {
            0 => null,
            1 => statements[0],
            _ => SyntaxFactory.Block(statements),
        };
    }
}
