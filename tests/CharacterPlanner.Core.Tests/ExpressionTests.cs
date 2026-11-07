using System.Collections.Immutable;
using CharacterPlanner.Core.Expressions;

namespace CharacterPlanner.Core.Tests;

// Expected behavior reviewed against internal/expr/expr_test.go and evaluate.go
// at go-final-0.1.0-beta.6. These tests do not execute the legacy engine.
public sealed class ExpressionTests
{
    private static readonly EvaluationScope Scope = new()
    {
        Stats = new Dictionary<string, double> { ["strength"] = 20, ["vigor"] = 30, ["zero"] = 0 },
        Curves = new Dictionary<string, ImmutableArray<CurvePoint>>
        { ["hp"] = [new(8, 300), new(30, 800), new(40, 1100), new(60, 1250)], ["empty"] = [] },
        Tags = new HashSet<string> { "sword", "light_armor" },
        SelectedTags = new HashSet<string> { "axe" },
        SelectedTalents = new HashSet<string> { "fury" },
        ContextValues = new Dictionary<string, double> { ["stacks"] = 3 }
    };

    [Theory]
    [InlineData("1 + 1", 2)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("10 - 3 - 2", 5)]
    [InlineData("100 / 5 / 2", 10)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("(2 ^ 3) ^ 2", 64)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("(-2) ^ 2", 4)]
    [InlineData("2 * -3", -6)]
    [InlineData("2 ^ -2", 0.25)]
    [InlineData(".5 + 1.", 1.5)]
    [InlineData("7 % 3", 1)]
    [InlineData("-7 % 3", -1)]
    [InlineData("3 % 2", 1)]
    [InlineData("10 / 0", 0)]
    [InlineData("10 % 0", 0)]
    [InlineData("1 + 2 > 2", 1)]
    [InlineData("1 < 2 && 3 < 4", 1)]
    [InlineData("1 > 2 || 3 < 4", 1)]
    [InlineData("!0", 1)]
    [InlineData("!5", 0)]
    [InlineData("1 == 1", 1)]
    [InlineData("1 != 1", 0)]
    [InlineData("'1' == 1", 0)]
    [InlineData("'x' == 'x'", 1)]
    [InlineData("!''", 1)]
    [InlineData("'x' && 3", 1)]
    [InlineData("0 && missing", 0)]
    [InlineData("1 || missing", 1)]
    [InlineData("0 ? missing : 7", 7)]
    [InlineData("1 ? 7 : missing", 7)]
    [InlineData("0 ? 1 : 0 ? 2 : 3", 3)]
    [InlineData("1 ? 1 : 0 ? 2 : 3", 1)]
    [InlineData("strength >= 20 ? 100 : 1", 100)]
    [InlineData("strength * 2", 40)]
    [InlineData("min(3, 5)", 3)]
    [InlineData("max(3, 5)", 5)]
    [InlineData("floor(3.7)", 3)]
    [InlineData("ceil(3.2)", 4)]
    [InlineData("round(3.5)", 4)]
    [InlineData("round(-3.5)", -4)]
    [InlineData("abs(-4)", 4)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("clamp(15, 0, 10)", 10)]
    [InlineData("clamp(-5, 0, 10)", 0)]
    [InlineData("clamp(5, 10, 0)", 0)]
    [InlineData("lerp(0, 100, 0.25)", 25)]
    [InlineData("floor(3.7, 8)", 3)]
    [InlineData("has_tag('sword')", 1)]
    [InlineData("has_tag('axe')", 0)]
    [InlineData("has_selected_tag('axe')", 1)]
    [InlineData("has_selected_tag('sword')", 0)]
    [InlineData("has_talent('fury')", 1)]
    [InlineData("has_talent('calm')", 0)]
    [InlineData("context_value('stacks') * 2", 6)]
    [InlineData("curve('hp', vigor)", 800)]
    [InlineData("curve('hp', 35)", 950)]
    [InlineData("curve('hp', 0)", 300)]
    [InlineData("curve('hp', 999)", 1250)]
    [InlineData("has_tag(')') + has_tag(',') + has_tag(':')", 0)]
    public void EvaluatesFrozenArithmeticAndScopeRules(string source, double expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(ExpressionParser.Parse(source), Scope));
    }

    [Theory]
    [InlineData("1 +", "Unexpected end of expression", 3)]
    [InlineData("(1 + 2", "Expected \")\"", 6)]
    [InlineData("1 + + ", "Unexpected operator \"+\"", 4)]
    [InlineData(")", "Unexpected \")\"", 0)]
    [InlineData("1 2", "Unexpected trailing input \"2\"", 2)]
    [InlineData("1 ? 2", "Expected ':' in conditional expression", 5)]
    [InlineData("curve('abc", "Unterminated string literal", 6)]
    [InlineData("a @ b", "Unexpected character \"@\"", 2)]
    [InlineData("1e3", "Unexpected trailing input \"e3\"", 1)]
    [InlineData("a ? 1 ':' 2", "Expected ':' in conditional expression", 6)]
    [InlineData("foo '(' 1)", "Unexpected trailing input \"(\"", 4)]
    [InlineData("min(1 ',' 2)", "Expected \")\"", 6)]
    public void ReportsParseErrorWithSourceAndBytePosition(string source, string expected, int position)
    {
        var error = Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(source));
        Assert.Equal(expected, error.Message);
        Assert.Equal(position, error.Position);
        Assert.Equal(source, error.ExpressionSource);
    }

    [Theory]
    [InlineData("floor()", "floor() expects one argument")]
    [InlineData("ceil()", "ceil() expects one argument")]
    [InlineData("round()", "round() expects one argument")]
    [InlineData("abs()", "abs() expects one argument")]
    [InlineData("sqrt()", "sqrt() expects one argument")]
    [InlineData("min('x')", "min() expects numeric arguments")]
    [InlineData("floor(1, 'x')", "floor() expects numeric arguments")]
    [InlineData("floor(min())", "floor() expects numeric arguments")]
    [InlineData("clamp(1)", "clamp() expects three arguments")]
    [InlineData("lerp(1, 2)", "lerp() expects three arguments")]
    [InlineData("has_tag()", "has_tag() expects a tag name")]
    [InlineData("has_selected_tag(1)", "has_selected_tag() expects a tag name")]
    [InlineData("has_talent('a','b')", "has_talent() expects one talent id string")]
    [InlineData("has_talent(1)", "has_talent() expects one talent id string")]
    [InlineData("context_value()", "context_value() expects one numeric context input id")]
    [InlineData("context_value(1)", "context_value() expects a string input id")]
    [InlineData("context_value('missing')", "Unknown or missing numeric context input \"missing\"")]
    [InlineData("curve(1, 2)", "curve() expects a curve name as its first argument")]
    [InlineData("curve('hp')", "curve() expects a numeric second argument")]
    [InlineData("curve('missing', 1)", "Unknown curve \"missing\"")]
    [InlineData("curve('empty', 1)", "Curve \"empty\" has no points")]
    [InlineData("missing + 1", "Unknown identifier \"missing\"")]
    [InlineData("bogus(1)", "Unknown function \"bogus\"")]
    [InlineData("'x' + 1", "Expected a number but got string")]
    [InlineData("'x'", "Expression did not evaluate to a number")]
    [InlineData("sqrt(-1)", "Expression did not evaluate to a number")]
    public void RejectsInvalidEvaluationWithoutCrashing(string source, string expected)
    {
        var error = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(ExpressionParser.Parse(source), Scope));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void PreservesNonFiniteReferenceBehavior()
    {
        Assert.Equal(double.PositiveInfinity, ExpressionEvaluator.Evaluate(ExpressionParser.Parse("min()"), Scope));
        Assert.Equal(double.NegativeInfinity, ExpressionEvaluator.Evaluate(ExpressionParser.Parse("max()"), Scope));
        Assert.Equal(double.PositiveInfinity, ExpressionEvaluator.Evaluate(ExpressionParser.Parse("10 ^ 1000"), Scope));
        Assert.Equal(0, ExpressionEvaluator.Evaluate(ExpressionParser.Parse("sqrt(-1) == sqrt(-1)"), Scope));
        Assert.Equal(1, ExpressionEvaluator.Evaluate(ExpressionParser.Parse("sqrt(-1) ? 1 : 0"), Scope));
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(ExpressionParser.Parse("context_value('stacks')"),
            Scope with { ContextValues = new Dictionary<string, double> { ["stacks"] = double.PositiveInfinity } }));
    }

    // The reference accepts unbounded nesting (ledger K03); C# reports it instead of overflowing the stack.
    [Theory]
    [InlineData("parentheses")]
    [InlineData("unary")]
    [InlineData("left-chain")]
    [InlineData("right-chain")]
    public void RejectsNestingBeyondTheLimitWithoutOverflowing(string shape)
    {
        const int count = 100_000;
        var source = shape switch
        {
            "parentheses" => new string('(', count) + "1" + new string(')', count),
            "unary" => new string('-', count) + "1",
            "left-chain" => string.Join('+', Enumerable.Repeat("1", count)),
            _ => string.Join('^', Enumerable.Repeat("1", count))
        };
        var error = Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(source));
        Assert.Equal($"Expression nesting exceeds {ExpressionParser.MaxDepth} levels", error.Message);
    }

    [Fact]
    public void AcceptsNestingUpToTheLimit()
    {
        var parens = ExpressionParser.MaxDepth - 1;
        Assert.Equal(1, ExpressionEvaluator.Evaluate(ExpressionParser.Parse(new string('(', parens) + "1" + new string(')', parens)), Scope));
        var chain = ExpressionParser.Parse(string.Join('+', Enumerable.Repeat("1", ExpressionParser.MaxDepth)));
        Assert.Equal(ExpressionParser.MaxDepth, chain.Depth());
        Assert.Equal(ExpressionParser.MaxDepth, ExpressionEvaluator.Evaluate(chain, Scope));
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(string.Join('+', Enumerable.Repeat("1", ExpressionParser.MaxDepth + 1))));
    }

    [Fact]
    public void CollectsAllDependenciesIncludingUnselectedBranches()
    {
        var expression = ExpressionParser.Parse("a + b * (c ? d : e) - min(f, curve('x', g))");
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], expression.Identifiers.Order(StringComparer.Ordinal));
        Assert.Equal(["curve", "min"], expression.FunctionNames.Order(StringComparer.Ordinal));
        Assert.Equal(["x"], expression.CurveNames);
        Assert.Empty(ExpressionParser.Parse("curve(curve_name, 1)").CurveNames);
    }

    [Fact]
    public void TokenizationPreservesQuotedTextAndUtf8Offsets()
    {
        var tokens = ExpressionParser.Tokenize("'é' >= .5");
        Assert.Equal(["é", ">=", ".5", ""], tokens.Select(token => token.Value));
        Assert.Equal([0, 5, 8, 10], tokens.Select(token => token.Position));
        Assert.Equal(@"a\nb", Assert.IsType<Expression.Text>(ExpressionParser.Parse("'a\\nb'")).Value);
    }

    [Fact]
    public void CurveInterpolationClampsAndSupportsFlatSegments()
    {
        ImmutableArray<CurvePoint> points = [new(0, 10), new(10, 10)];
        Assert.Equal(10, ExpressionEvaluator.EvaluateCurve(points, 5));
        Assert.Equal(10, ExpressionEvaluator.EvaluateCurve([new(0, 10)], 999));
        Assert.Throws<ArgumentException>(() => ExpressionEvaluator.EvaluateCurve([], 1));
    }
}
