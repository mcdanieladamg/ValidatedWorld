namespace ValidatedWorld.Core;

public readonly struct ProjectId : IEquatable<ProjectId>, IComparable<ProjectId>
{
    private readonly string? _value;

    public ProjectId(string value)
    {
        _value = StableIdValidation.Validate(value, nameof(value));
    }

    public string Value => _value ?? throw new InvalidOperationException("The project ID is uninitialized.");

    public bool IsInitialized => _value is not null;

    public int CompareTo(ProjectId other) => StringComparer.Ordinal.Compare(_value, other._value);

    public bool Equals(ProjectId other) => StringComparer.Ordinal.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is ProjectId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value ?? string.Empty);

    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(ProjectId left, ProjectId right) => left.Equals(right);

    public static bool operator !=(ProjectId left, ProjectId right) => !left.Equals(right);

    public static bool operator <(ProjectId left, ProjectId right) => left.CompareTo(right) < 0;

    public static bool operator >(ProjectId left, ProjectId right) => left.CompareTo(right) > 0;
}

public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
{
    private readonly string? _value;

    public EntityId(string value)
    {
        _value = StableIdValidation.Validate(value, nameof(value));
    }

    public string Value => _value ?? throw new InvalidOperationException("The entity ID is uninitialized.");

    public bool IsInitialized => _value is not null;

    public int CompareTo(EntityId other) => StringComparer.Ordinal.Compare(_value, other._value);

    public bool Equals(EntityId other) => StringComparer.Ordinal.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value ?? string.Empty);

    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);

    public static bool operator <(EntityId left, EntityId right) => left.CompareTo(right) < 0;

    public static bool operator >(EntityId left, EntityId right) => left.CompareTo(right) > 0;
}

internal static class StableIdValidation
{
    public static string Validate(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An ID cannot be empty or whitespace-only.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("An ID cannot contain control characters.", parameterName);
        }

        return value;
    }
}
