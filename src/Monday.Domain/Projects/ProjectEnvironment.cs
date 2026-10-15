using Monday.Domain.Common;

namespace Monday.Domain.Projects;

public enum EnvironmentKind
{
    Development,
    Staging,
    Production,
}

/// <summary>Anything that would change an app or its infrastructure.</summary>
public enum WriteAction
{
    TestRequest,
    CodeChange,
    DataChange,
    ConfigChange,
    ScaleUp,
}

public readonly record struct EnvironmentId(Guid Value)
{
    public static EnvironmentId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>One deployment of a project: development, staging or production.</summary>
public sealed class ProjectEnvironment
{
    internal ProjectEnvironment(EnvironmentId id, string name, EnvironmentKind kind)
    {
        Id = id;
        Name = Guard.NotBlank(name, "environment.name_required", "An environment needs a name", 40);
        Kind = kind;
    }

    public EnvironmentId Id { get; }

    public string Name { get; }

    public EnvironmentKind Kind { get; private set; }

    /// <summary>Production can be watched, never touched.</summary>
    public bool IsReadOnly => Kind == EnvironmentKind.Production;

    internal void ChangeKind(EnvironmentKind kind) => Kind = kind;
}

/// <summary>
/// The production rule from the Monday plan: "PROD is PROD". Every write path in Monday asks
/// this guard first, and the guard has no override.
/// </summary>
public static class ProductionGuard
{
    public static void EnsureWritable(ProjectEnvironment environment, WriteAction action)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsReadOnly)
        {
            throw new ProductionReadOnlyException(environment.Name, action);
        }
    }
}

public sealed class ProductionReadOnlyException(string environment, WriteAction action)
    : DomainException(
        "production.read_only",
        $"'{environment}' is production: Monday only monitors it. {action} must be done locally or in development.")
{
    public WriteAction Action { get; } = action;
}
