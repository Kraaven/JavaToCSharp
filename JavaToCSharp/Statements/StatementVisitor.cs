using com.github.javaparser.ast.stmt;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public abstract class StatementVisitor<T> : StatementVisitor
    where T : Statement
{
    public abstract StatementSyntax? Visit(ConversionContext context, T statement);

    protected sealed override StatementSyntax? Visit(ConversionContext context, Statement statement)
    {
        return Visit(context, (T)statement);
    }
}

public abstract class StatementVisitor
{
    private static readonly IDictionary<Type, StatementVisitor> _visitors;

    static StatementVisitor()
    {
        _visitors = new Dictionary<Type, StatementVisitor>
        {
            { typeof(AssertStmt), new AssertStatementVisitor() },
            { typeof(BlockStmt), new BlockStatementVisitor() },
            { typeof(BreakStmt), new BreakStatementVisitor() },
            { typeof(ContinueStmt), new ContinueStatementVisitor() },
            { typeof(DoStmt), new DoStatementVisitor() },
            { typeof(ExpressionStmt), new ExpressionStatementVisitor() },
            { typeof(ForEachStmt), new ForEachStatementVisitor() },
            { typeof(ForStmt), new ForStatementVisitor() },
            { typeof(IfStmt), new IfStatementVisitor() },
            { typeof(LabeledStmt), new LabeledStatementVisitor() },
            { typeof(ReturnStmt), new ReturnStatementVisitor() },
            { typeof(SwitchStmt), new SwitchStatementVisitor() },
            { typeof(SynchronizedStmt), new SynchronizedStatementVisitor() },
            { typeof(ThrowStmt), new ThrowStatementVisitor() },
            { typeof(TryStmt), new TryStatementVisitor() },
            { typeof(WhileStmt), new WhileStatementVisitor() },
            { typeof(EmptyStmt), new EmptyStatementVisitor() },
            { typeof(LocalClassDeclarationStmt), new TypeDeclarationStatementVisitor() },
            { typeof(LocalRecordDeclarationStmt), new LocalRecordDeclarationStatementVisitor() },
            { typeof(YieldStmt), new YieldStatementVisitor() }
        };
    }

    protected abstract StatementSyntax? Visit(ConversionContext context, Statement statement);

    public static List<StatementSyntax> VisitStatements(ConversionContext context, IEnumerable<Statement>? statements)
    {
        if (statements is null)
        {
            return [];
        }

        var result = new List<StatementSyntax>();

        foreach (var statement in statements)
        {
            var syntax = VisitStatement(context, statement);
            if (syntax != null)
            {
                // Flatten synthetic blocks that only exist to carry pending declarations,
                // but only when the block is trivially small (just pending statements).
                // Blocks containing the actual converted statement should stay wrapped
                // to preserve correct scope nesting.
                if (syntax is BlockSyntax block && statement is not BlockStmt
                    && block.Statements.Count <= 2)
                {
                    result.AddRange(block.Statements);
                }
                else
                {
                    result.Add(syntax);
                }
            }
        }

        return result;
    }

    public static StatementSyntax? VisitStatement(ConversionContext context, Statement statement)
    {
        if (!_visitors.TryGetValue(statement.GetType(), out var visitor))
        {
            var message = $"Statement visitor not implemented for statement `{statement}`, `{statement.getBegin()}` type `{statement.GetType()}`.";
            throw new InvalidOperationException(message);
        }

        var syntax = visitor.Visit(context, statement).WithJavaComments(context, statement);
        var pending = context.DrainPendingStatements();

        if (pending.Count == 0)
        {
            return syntax;
        }

        if (syntax is null)
        {
            return pending.Count == 1 ? pending[0] : SyntaxFactory.Block(pending);
        }

        return SyntaxFactory.Block(pending.Append(syntax));
    }
}
