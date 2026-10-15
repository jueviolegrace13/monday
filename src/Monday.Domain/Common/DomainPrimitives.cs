namespace Monday.Domain.Common;

/// <summary>Thrown when an operation would break a domain rule.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    /// <summary>Stable, machine-readable reason (for example "production.read_only").</summary>
    public string Code { get; } = code;
}

/// <summary>Something that happened in the domain that other parts may react to.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Base for aggregates: the consistency boundary that records domain events.</summary>
public abstract class AggregateRoot<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _events = [];

    protected AggregateRoot(TId id) => Id = id;

    public TId Id { get; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);

    public void ClearDomainEvents() => _events.Clear();
}

/// <summary>Small guard helpers so invariants read as one line.</summary>
public static class Guard
{
    public static string NotBlank(string? value, string code, string message, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(code, message);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException(code, $"{message} (at most {maxLength} characters).");
        }

        return trimmed;
    }

    public static void That(bool condition, string code, string message)
    {
        if (!condition)
        {
            throw new DomainException(code, message);
        }
    }
}

/// <summary>Who did something: a signed-in person, a Claude session, or an app reporting through an API key.</summary>
public readonly record struct UserId
{
    public UserId(string value) =>
        Value = Guard.NotBlank(value, "user.id_required", "A user id is required", 128);

    public string Value { get; }

    public override string ToString() => Value;
}
