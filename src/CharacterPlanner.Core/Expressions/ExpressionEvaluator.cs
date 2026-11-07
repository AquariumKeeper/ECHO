using System.Collections.Immutable;
using static CharacterPlanner.Core.Expressions.ExpressionParser;

namespace CharacterPlanner.Core.Expressions;

public readonly record struct CurvePoint(double X, double Y);

public sealed record EvaluationScope
{
    public IReadOnlyDictionary<string, double> Stats { get; init; } = ImmutableDictionary<string, double>.Empty;
    public IReadOnlyDictionary<string, ImmutableArray<CurvePoint>> Curves { get; init; } = ImmutableDictionary<string, ImmutableArray<CurvePoint>>.Empty;
    public IReadOnlySet<string> Tags { get; init; } = ImmutableHashSet<string>.Empty;
    public IReadOnlySet<string> SelectedTags { get; init; } = ImmutableHashSet<string>.Empty;
    public IReadOnlySet<string> SelectedTalents { get; init; } = ImmutableHashSet<string>.Empty;
    public IReadOnlyDictionary<string, double> ContextValues { get; init; } = ImmutableDictionary<string, double>.Empty;
}

public static class ExpressionEvaluator
{
    public static ImmutableArray<string> FunctionNames { get; } =
        ["min", "max", "floor", "ceil", "round", "abs", "sqrt", "clamp", "lerp", "curve", "has_tag", "has_selected_tag", "has_talent", "context_value"];

    public static double Evaluate(Expression expression, EvaluationScope scope)
    {
        var result = EvaluateNode(expression, scope);
        // The frozen Go implementation rejects NaN but permits infinity here.
        // Numeric builtins and context inputs apply stricter finite checks.
        if (result is not double number || double.IsNaN(number))
        { throw Error("Expression did not evaluate to a number", expression); }
        return number;
    }

    public static double EvaluateCurve(ImmutableArray<CurvePoint> points, double x)
    {
        if (points.IsDefaultOrEmpty) { throw new ArgumentException("A curve needs at least one point.", nameof(points)); }
        if (x <= points[0].X) { return points[0].Y; }
        if (x >= points[^1].X) { return points[^1].Y; }
        for (var i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (x >= a.X && x <= b.X)
            { return b.X == a.X ? b.Y : a.Y + (x - a.X) / (b.X - a.X) * (b.Y - a.Y); }
        }
        return points[^1].Y;
    }

    private static ExpressionException Error(string message, Expression expression) => new(message, 0, expression switch
    {
        Expression.Number => "number",
        Expression.Text => "string",
        Expression.Identifier => "ident",
        Expression.Unary => "unary",
        Expression.Binary => "binary",
        Expression.Conditional => "conditional",
        _ => "call"
    });

    private static double Number(object value, Expression expression) => value is double number ? number :
        throw Error("Expected a number but got string", expression);
    private static bool Truthy(object value) => value is double number ? number != 0 : value is string text && text.Length != 0;
    private static double Boolean(bool value) => value ? 1d : 0d;

    private static object EvaluateNode(Expression expression, EvaluationScope scope)
    {
        switch (expression)
        {
            case Expression.Number number: return number.Value;
            case Expression.Text text: return text.Value;
            case Expression.Identifier id:
                return scope.Stats.TryGetValue(id.Name, out var value) ? value :
                    throw new ExpressionException($"Unknown identifier {Quote(id.Name)}", 0, id.Name);
            case Expression.Unary unary:
                var operand = EvaluateNode(unary.Operand, scope);
                return unary.Operator == "-" ? -Number(operand, unary) : Boolean(!Truthy(operand));
            case Expression.Conditional conditional:
                return EvaluateNode(Truthy(EvaluateNode(conditional.Test, scope)) ? conditional.Consequent : conditional.Alternate, scope);
            case Expression.Binary binary:
                var left = EvaluateNode(binary.Left, scope);
                if (binary.Operator == "&&") { return Boolean(Truthy(left) && Truthy(EvaluateNode(binary.Right, scope))); }
                if (binary.Operator == "||") { return Boolean(Truthy(left) || Truthy(EvaluateNode(binary.Right, scope))); }
                var right = EvaluateNode(binary.Right, scope);
                // Boxed Double.Equals treats NaN as equal; Go's numeric == does not.
                var equal = left is double ld && right is double rd ? ld == rd : left.Equals(right);
                if (binary.Operator == "==") { return Boolean(equal); }
                if (binary.Operator == "!=") { return Boolean(!equal); }
                var a = Number(left, binary);
                var b = Number(right, binary);
                return binary.Operator switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" => b == 0 ? 0d : a / b,
                    "%" => b == 0 ? 0d : a % b,
                    "^" => Math.Pow(a, b),
                    "<" => Boolean(a < b),
                    "<=" => Boolean(a <= b),
                    ">" => Boolean(a > b),
                    ">=" => Boolean(a >= b),
                    _ => throw Error($"Unsupported operator {binary.Operator}", binary)
                };
            case Expression.Call call:
                if (!FunctionNames.Contains(call.Name)) { throw new ExpressionException($"Unknown function {Quote(call.Name)}", 0, call.Name); }
                return Call(call, call.Arguments.Select(arg => EvaluateNode(arg, scope)).ToArray(), scope);
            default: throw Error("Unsupported expression node", expression);
        }
    }

    private static double Call(Expression.Call call, object[] args, EvaluationScope scope)
    {
        string StringArgument(string message)
        {
            if (args.Length == 0 || args[0] is not string text) { throw Error(message, call); }
            return text;
        }
        switch (call.Name)
        {
            case "curve":
                var name = StringArgument("curve() expects a curve name as its first argument");
                if (args.Length < 2 || args[1] is not double x) { throw Error("curve() expects a numeric second argument", call); }
                if (!scope.Curves.TryGetValue(name, out var points)) { throw Error($"Unknown curve {Quote(name)}", call); }
                if (points.IsDefaultOrEmpty) { throw Error($"Curve {Quote(name)} has no points", call); }
                return EvaluateCurve(points, x);
            case "has_tag": return Boolean(scope.Tags.Contains(StringArgument("has_tag() expects a tag name")));
            case "has_selected_tag": return Boolean(scope.SelectedTags.Contains(StringArgument("has_selected_tag() expects a tag name")));
            case "has_talent":
                if (args.Length != 1) { throw Error("has_talent() expects one talent id string", call); }
                return Boolean(scope.SelectedTalents.Contains(StringArgument("has_talent() expects one talent id string")));
            case "context_value":
                if (args.Length != 1) { throw Error("context_value() expects one numeric context input id", call); }
                var id = StringArgument("context_value() expects a string input id");
                if (!scope.ContextValues.TryGetValue(id, out var context)) { throw Error($"Unknown or missing numeric context input {Quote(id)}", call); }
                if (!double.IsFinite(context)) { throw Error($"Numeric context input {Quote(id)} must be finite", call); }
                return context;
        }
        var nums = new double[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not double number || !double.IsFinite(number)) { throw Error($"{call.Name}() expects numeric arguments", call); }
            nums[i] = number;
        }
        if (call.Name == "min") { return nums.Length == 0 ? double.PositiveInfinity : nums.Min(); }
        if (call.Name == "max") { return nums.Length == 0 ? double.NegativeInfinity : nums.Max(); }
        if (call.Name is "clamp" or "lerp")
        {
            if (nums.Length < 3) { throw Error($"{call.Name}() expects three arguments", call); }
            return call.Name == "clamp" ? Math.Min(Math.Max(nums[0], nums[1]), nums[2]) : nums[0] + (nums[1] - nums[0]) * nums[2];
        }
        if (nums.Length == 0) { throw Error($"{call.Name}() expects one argument", call); }
        return call.Name switch
        {
            "floor" => Math.Floor(nums[0]),
            "ceil" => Math.Ceiling(nums[0]),
            "round" => Math.Round(nums[0], MidpointRounding.AwayFromZero),
            "abs" => Math.Abs(nums[0]),
            "sqrt" => Math.Sqrt(nums[0]),
            _ => throw Error($"Unknown function {Quote(call.Name)}", call)
        };
    }
}
