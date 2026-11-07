using CharacterPlanner.Core.Expressions;

namespace CharacterPlanner.Core.Engine;

/// <summary>
/// The one way resolution evaluates a pack expression (M1). Validation rejects malformed calls, so what remains
/// at run time is an error the evaluator reports (<see cref="ExpressionException"/>) or a non-finite result; both
/// come back as a message for a diagnostic. Other exceptions are programming errors, such as a batch that doesn't
/// belong to its compiled pack, and are deliberately left to propagate.
/// </summary>
internal static class EvaluationGuard
{
    /// <summary>The value, or 0 with <paramref name="error"/> set when evaluation fails or the result isn't finite.</summary>
    public static bool TryEvaluate(Expression expression, EvaluationScope scope, string kind, out double value, out string error)
    {
        value = 0;
        double result;
        try { result = ExpressionEvaluator.Evaluate(expression, scope); }
        catch (ExpressionException exception) { error = exception.Message; return false; }
        if (!double.IsFinite(result)) { error = kind + " returned a non-finite value"; return false; }
        (value, error) = (result, "");
        return true;
    }
}
