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

    /// <summary>Production is off limits: Monday neither watches nor touches it.</summary>
    public bool IsReadOnly => Kind == EnvironmentKind.Production;

    internal void ChangeKind(EnvironmentKind kind) => Kind = kind;
}

/// <summary>
/// The production rule: for now Monday only assists engineers on their local codebase, so production is off limits.
/// No production environment can be added, keyed or sent from. Every write path still asks <see cref="EnsureWritable"/>
/// first, for environments stored before the rule. Neither check has an override.
/// </summary>
public static class ProductionGuard
{
    public static void EnsureNotProduction(EnvironmentKind kind) =>
        Guard.That(
            kind != EnvironmentKind.Production,
            "production.off_limits",
            "Production is off limits: Monday works only on local and development environments for now.");

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
