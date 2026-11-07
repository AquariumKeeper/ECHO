using System.Collections.Immutable;

namespace CharacterPlanner.Core.Expressions;

public abstract record Expression
{
    private Expression() { }

    public sealed record Number(double Value) : Expression;
    public sealed record Text(string Value) : Expression;
    public sealed record Identifier(string Name) : Expression;
    public sealed record Unary(string Operator, Expression Operand) : Expression;
    public sealed record Binary(string Operator, Expression Left, Expression Right) : Expression;
    public sealed record Conditional(Expression Test, Expression Consequent, Expression Alternate) : Expression;
    public sealed record Call(string Name, ImmutableArray<Expression> Arguments) : Expression;

    private ImmutableArray<Expression> Children() => this switch
    {
        Unary unary => [unary.Operand],
        Binary binary => [binary.Left, binary.Right],
        Conditional conditional => [conditional.Test, conditional.Consequent, conditional.Alternate],
        Call call => call.Arguments,
        _ => []
    };

    // Pre-order walk with an explicit stack, so tree depth never consumes call stack.
    public IEnumerable<Expression> DescendantsAndSelf()
    {
        var pending = new Stack<Expression>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            yield return node;
            var children = node.Children();
            for (var i = children.Length - 1; i >= 0; i--) { pending.Push(children[i]); }
        }
    }

    public int Depth()
    {
        var deepest = 0;
        var pending = new Stack<(Expression Node, int Depth)>();
        pending.Push((this, 1));
        while (pending.TryPop(out var item))
        {
            deepest = Math.Max(deepest, item.Depth);
            foreach (var child in item.Node.Children()) { pending.Push((child, item.Depth + 1)); }
        }
        return deepest;
    }

    public ImmutableHashSet<string> Identifiers => DescendantsAndSelf().OfType<Identifier>()
        .Select(node => node.Name).ToImmutableHashSet(StringComparer.Ordinal);

    public ImmutableHashSet<string> FunctionNames => DescendantsAndSelf().OfType<Call>()
        .Select(node => node.Name).ToImmutableHashSet(StringComparer.Ordinal);

    public ImmutableHashSet<string> CurveNames => LiteralFirstArguments("curve");

    /// <summary>
    /// The numeric assumptions the expression reads through <c>context_value("id")</c>. Resolution asks on every edit,
    /// so the answer is kept per expression.
    /// </summary>
    public ImmutableHashSet<string> ContextReads => ContextReadCache.GetValue(this, static expression => expression.LiteralFirstArguments("context_value"));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Expression, ImmutableHashSet<string>> ContextReadCache = [];

    /// <summary>The literal text first argument of every call to <paramref name="function"/>.</summary>
    private ImmutableHashSet<string> LiteralFirstArguments(string function) => DescendantsAndSelf().OfType<Call>()
        .Where(call => call.Name == function && call.Arguments is [Text, ..])
        .Select(call => ((Text)call.Arguments[0]).Value).ToImmutableHashSet(StringComparer.Ordinal);
}

public sealed class ExpressionException(string message, int position, string source) : Exception(message)
{
    // The reference diagnostics report UTF-8 byte offsets, not UTF-16 indices.
    public int Position { get; } = position;
    public string ExpressionSource { get; } = source;
}
