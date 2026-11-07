using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Diagnostics;

namespace CharacterPlanner.Core.Expressions;

public enum TokenKind { Number, Text, Identifier, Operator, Punctuation, End }
public readonly record struct Token(TokenKind Kind, string Value, int Position);

public static class ExpressionParser
{
    public static Expression Parse(string source) => new Parser(source, Tokenize(source)).Parse();

    public static ImmutableArray<Token> Tokenize(string source)
    {
        var tokens = ImmutableArray.CreateBuilder<Token>();
        var bytes = Encoding.UTF8.GetBytes(source);
        var i = 0;
        bool Digit(byte b) => b is >= (byte)'0' and <= (byte)'9';
        bool Start(byte b) => b is (byte)'_' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (char.IsWhiteSpace((char)b)) { i++; continue; }
            var start = i;
            if (Digit(b) || b == '.' && i + 1 < bytes.Length && Digit(bytes[i + 1]))
            {
                while (i < bytes.Length && Digit(bytes[i])) { i++; }
                if (i < bytes.Length && bytes[i] == '.')
                {
                    i++;
                    while (i < bytes.Length && Digit(bytes[i])) { i++; }
                }
                tokens.Add(new(TokenKind.Number, Encoding.UTF8.GetString(bytes[start..i]), start));
            }
            else if (b is (byte)'\'' or (byte)'"')
            {
                i++;
                while (i < bytes.Length && bytes[i] != b) { i++; }
                if (i == bytes.Length) { throw new ExpressionException("Unterminated string literal", start, source); }
                tokens.Add(new(TokenKind.Text, Encoding.UTF8.GetString(bytes[(start + 1)..i]), start));
                i++;
            }
            else if (Start(b))
            {
                while (i < bytes.Length && (Start(bytes[i]) || Digit(bytes[i]))) { i++; }
                tokens.Add(new(TokenKind.Identifier, Encoding.UTF8.GetString(bytes[start..i]), start));
            }
            else if (b is (byte)'(' or (byte)')' or (byte)',')
            {
                tokens.Add(new(TokenKind.Punctuation, ((char)b).ToString(), i++));
            }
            else
            {
                var pair = i + 1 < bytes.Length ? Encoding.UTF8.GetString(bytes[i..(i + 2)]) : "";
                if (pair is "==" or "!=" or "<=" or ">=" or "&&" or "||")
                {
                    tokens.Add(new(TokenKind.Operator, pair, i));
                    i += 2;
                }
                else if ("+-*/%^<>!?:".Contains((char)b))
                {
                    tokens.Add(new(TokenKind.Operator, ((char)b).ToString(), i++));
                }
                else { throw new ExpressionException($"Unexpected character {Quote(((char)b).ToString())}", i, source); }
            }
        }
        tokens.Add(new(TokenKind.End, "", bytes.Length));
        return tokens.ToImmutable();
    }

    // The reference parser has no nesting limit and relies on Go's growable stacks.
    // Deeper input would overflow the .NET stack, which no caller can catch.
    public const int MaxDepth = 256;
    private static readonly string TooDeep = $"Expression nesting exceeds {MaxDepth} levels";

    internal static string Quote(string value) => ReferenceText.Quote(value);

    private sealed class Parser(string source, ImmutableArray<Token> tokens)
    {
        private int index;
        private Token Peek => tokens[index];
        private Token Next() { var token = Peek; if (token.Kind != TokenKind.End) { index++; } return token; }
        private ExpressionException Fail(string message, Token token) => new(message, token.Position, source);
        private bool At(TokenKind kind, string value) => Peek.Kind == kind && Peek.Value == value;
        private int depth;

        public Expression Parse()
        {
            var result = ParseExpression(0);
            if (Peek.Kind != TokenKind.End) { throw Fail($"Unexpected trailing input {Quote(Peek.Value)}", Peek); }
            // Left-associative chains build deep trees without deep parser recursion.
            if (result.Depth() > MaxDepth) { throw new ExpressionException(TooDeep, 0, source); }
            return result;
        }

        private void Consume(string value)
        {
            if (!At(TokenKind.Punctuation, value)) { throw Fail($"Expected {Quote(value)}", Peek); }
            Next();
        }

        private Expression ParseExpression(double minimum)
        {
            if (depth == MaxDepth) { throw Fail(TooDeep, Peek); }
            depth++;
            try { return ParseOperators(minimum); }
            finally { depth--; }
        }

        private Expression ParseOperators(double minimum)
        {
            var left = Prefix();
            while (Peek.Kind == TokenKind.Operator)
            {
                var token = Peek;
                if (token.Value == "?")
                {
                    if (minimum > 0.5) { break; }
                    Next();
                    var consequent = ParseExpression(0);
                    if (!At(TokenKind.Operator, ":")) { throw Fail("Expected ':' in conditional expression", Peek); }
                    Next();
                    left = new Expression.Conditional(left, consequent, ParseExpression(0.5));
                    continue;
                }
                var power = token.Value switch
                {
                    "||" => 1,
                    "&&" => 2,
                    "==" or "!=" => 3,
                    "<" or "<=" or ">" or ">=" => 4,
                    "+" or "-" => 5,
                    "*" or "/" or "%" => 6,
                    "^" => 7,
                    _ => -1
                };
                if (power < minimum) { break; }
                Next();
                left = new Expression.Binary(token.Value, left, ParseExpression(power + (token.Value == "^" ? 0 : 1)));
            }
            return left;
        }

        private Expression Prefix()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Number:
                    if (!double.TryParse(token.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    { throw Fail($"Invalid number {Quote(token.Value)}", token); }
                    return new Expression.Number(number);
                case TokenKind.Text: return new Expression.Text(token.Value);
                case TokenKind.Identifier:
                    if (!At(TokenKind.Punctuation, "(")) { return new Expression.Identifier(token.Value); }
                    Next();
                    var args = ImmutableArray.CreateBuilder<Expression>();
                    if (!At(TokenKind.Punctuation, ")"))
                    {
                        while (true)
                        {
                            args.Add(ParseExpression(0));
                            if (!At(TokenKind.Punctuation, ",")) { break; }
                            Next();
                        }
                    }
                    Consume(")");
                    return new Expression.Call(token.Value, args.ToImmutable());
                case TokenKind.Operator:
                    if (token.Value is "-" or "!") { return new Expression.Unary(token.Value, ParseExpression(7)); }
                    throw Fail($"Unexpected operator {Quote(token.Value)}", token);
                case TokenKind.Punctuation:
                    if (token.Value != "(") { throw Fail($"Unexpected {Quote(token.Value)}", token); }
                    var inner = ParseExpression(0);
                    Consume(")");
                    return inner;
                default: throw Fail("Unexpected end of expression", token);
            }
        }
    }
}
