using System.Collections.Frozen;

namespace Monday.Domain.Access;

/// <summary>A person's role on one project. Access is always per project.</summary>
public enum Role
{
    /// <summary>The daily-activity screen: the graph alive, nothing underneath.</summary>
    Monitor = 0,

    /// <summary>High-level debugging, traffic replay and user-session monitoring.</summary>
    Debugger = 1,

    /// <summary>Everything a Debugger can do, plus members, roles, keys and archive restores.</summary>
    Admin = 2,

    /// <summary>Full control, including approvals and deleting the project.</summary>
    Owner = 3,
}

/// <summary>Everything a person can be allowed to do in Monday.</summary>
public enum Permission
{
    ViewLiveGraph,
    ViewTrafficTotals,
    ViewCode,
    InspectRequests,
    SendTestRequests,
    ReplayAppTraffic,
    ReplayUserTraffic,
    RestoreArchive,
    ChatAsk,
    ChatAct,
    ApproveDecisions,
    ManageMembersAndKeys,
    DeleteProject,
}

/// <summary>
/// The role table from the Monday plan. Roles are named bundles of permissions, so a custom
/// role later is a new entry here, not new checks spread through the code.
/// </summary>
public static class RolePermissions
{
    private static readonly FrozenDictionary<Role, IReadOnlySet<Permission>> Map =
        new Dictionary<Role, IReadOnlySet<Permission>>
        {
            [Role.Monitor] = new HashSet<Permission>
            {
                Permission.ViewLiveGraph,
                Permission.ViewTrafficTotals,
                Permission.ChatAsk,
            },
            [Role.Debugger] = new HashSet<Permission>
            {
                Permission.ViewLiveGraph,
                Permission.ViewTrafficTotals,
                Permission.ViewCode,
                Permission.InspectRequests,
                Permission.SendTestRequests,
                Permission.ReplayAppTraffic,
                Permission.ReplayUserTraffic,
                Permission.ChatAsk,
                Permission.ChatAct,
            },
            [Role.Admin] = new HashSet<Permission>
            {
                Permission.ViewLiveGraph,
                Permission.ViewTrafficTotals,
                Permission.ViewCode,
                Permission.InspectRequests,
                Permission.SendTestRequests,
                Permission.ReplayAppTraffic,
                Permission.ReplayUserTraffic,
                Permission.RestoreArchive,
                Permission.ChatAsk,
                Permission.ChatAct,
                Permission.ManageMembersAndKeys,
            },
            [Role.Owner] = Enum.GetValues<Permission>().ToHashSet(),
        }.ToFrozenDictionary();

    public static bool Allows(Role role, Permission permission) => Map[role].Contains(permission);

    public static IReadOnlySet<Permission> For(Role role) => Map[role];

    /// <summary>Permissions whose every use is written to the audit log.</summary>
    public static bool IsAudited(Permission permission) =>
        permission is Permission.ReplayUserTraffic or Permission.RestoreArchive or Permission.ManageMembersAndKeys;
}
