using com.github.javaparser.ast.stmt;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public class SwitchStatementVisitor : StatementVisitor<SwitchStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, SwitchStmt switchStmt)
        => SwitchConversion.ConvertSwitchStatement(context, switchStmt);
}
