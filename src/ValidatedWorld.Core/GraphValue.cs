using System.Globalization;
using System.Text.RegularExpressions;

namespace ValidatedWorld.Core;

public enum GraphValueKind
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Symbol,
    Instant,
}

/// <summary>A deterministic scalar value suitable for graph metadata.</summary>
public readonly struct GraphValue : IEquatable<GraphValue>
{
    private static readonly Regex CanonicalDecimal = new(
        "\\A-?(?:0|[1-9][0-9]*)(?:\\.[0-9]*[1-9])?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Regex.InfiniteMatchTimeout);

    private readonly string? _text;
    private readonly long _integer;
    private readonly bool _boolean;
    private readonly DateTimeOffset _instant;

    private GraphValue(GraphValueKind kind, string? text, long integer, bool boolean, DateTimeOffset instant)
    {
        Kind = kind;
        _text = text;
        _integer = integer;
        _boolean = boolean;
        _instant = instant;
    }

    public GraphValueKind Kind { get; }

    public string TextValue => Kind == GraphValueKind.Text
        ? _text!
        : throw new InvalidOperationException("This graph value is not text.");

    public long IntegerValue => Kind == GraphValueKind.Integer
        ? _integer
        : throw new InvalidOperationException("This graph value is not an integer.");

    public string DecimalValue => Kind == GraphValueKind.Decimal
        ? _text!
        : throw new InvalidOperationException("This graph value is not a decimal.");

    public bool BooleanValue => Kind == GraphValueKind.Boolean
        ? _boolean
        : throw new InvalidOperationException("This graph value is not Boolean.");

    public string SymbolValue => Kind == GraphValueKind.Symbol
        ? _text!
        : throw new InvalidOperationException("This graph value is not a symbol.");

    public DateTimeOffset InstantValue => Kind == GraphValueKind.Instant
        ? _instant
        : throw new InvalidOperationException("This graph value is not an instant.");

    internal bool IsInitialized => Kind switch
    {
        GraphValueKind.Text or GraphValueKind.Decimal or GraphValueKind.Symbol => _text is not null,
        GraphValueKind.Integer or GraphValueKind.Boolean or GraphValueKind.Instant => true,
        _ => false,
    };

    public static GraphValue FromText(string value)
    {
        return new GraphValue(
            GraphValueKind.Text,
            GraphTextValidation.Validate(value, nameof(value)),
            0,
            false,
            default);
    }

    public static GraphValue FromInteger(long value) => new(GraphValueKind.Integer, null, value, false, default);

    public static GraphValue FromDecimal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || !CanonicalDecimal.IsMatch(value))
        {
            throw new ArgumentException("The decimal is not in canonical base-10 form.", nameof(value));
        }

        if (value.StartsWith("-0", StringComparison.Ordinal) &&
            (value.Length == 2 || value[2..].All(c => c == '0' || c == '.')))
        {
            throw new ArgumentException("Negative zero is not canonical.", nameof(value));
        }

        return new GraphValue(GraphValueKind.Decimal, value, 0, false, default);
    }

    public static GraphValue FromBoolean(bool value) => new(GraphValueKind.Boolean, null, 0, value, default);

    public static GraphValue FromSymbol(string value)
    {
        return new GraphValue(
            GraphValueKind.Symbol,
            GraphTextValidation.ValidateMetadata(value, nameof(value)),
            0,
            false,
            default);
    }

    public static GraphValue FromInstant(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("An instant must have a zero UTC offset.", nameof(value));
        }

        return new GraphValue(GraphValueKind.Instant, null, 0, false, value);
    }

    public bool Equals(GraphValue other)
    {
        return Kind == other.Kind && Kind switch
        {
            GraphValueKind.Text or GraphValueKind.Decimal or GraphValueKind.Symbol =>
                StringComparer.Ordinal.Equals(_text, other._text),
            GraphValueKind.Integer => _integer == other._integer,
            GraphValueKind.Boolean => _boolean == other._boolean,
            GraphValueKind.Instant => _instant == other._instant,
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is GraphValue other && Equals(other);

    public override int GetHashCode()
    {
        return Kind switch
        {
            GraphValueKind.Text or GraphValueKind.Decimal or GraphValueKind.Symbol =>
                HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(_text ?? string.Empty)),
            GraphValueKind.Integer => HashCode.Combine(Kind, _integer),
            GraphValueKind.Boolean => HashCode.Combine(Kind, _boolean),
            GraphValueKind.Instant => HashCode.Combine(Kind, _instant),
            _ => 0,
        };
    }

    public override string ToString()
    {
        return Kind switch
        {
            GraphValueKind.Text => TextValue,
            GraphValueKind.Integer => _integer.ToString(CultureInfo.InvariantCulture),
            GraphValueKind.Decimal => DecimalValue,
            GraphValueKind.Boolean => _boolean ? "true" : "false",
            GraphValueKind.Symbol => SymbolValue,
            GraphValueKind.Instant => _instant.ToString("O", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("The graph value is uninitialized."),
        };
    }

    public static bool operator ==(GraphValue left, GraphValue right) => left.Equals(right);

    public static bool operator !=(GraphValue left, GraphValue right) => !left.Equals(right);
}
