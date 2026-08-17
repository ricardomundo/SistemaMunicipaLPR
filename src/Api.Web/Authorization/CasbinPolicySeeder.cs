using Casbin;

namespace Api.Web.Authorization;

/// <summary>
/// Seeds the baseline role -> (obj, act) permission matrix on first run. Idempotent: only
/// adds a policy row if it doesn't already exist, so re-running on every startup is safe
/// and later admin-driven policy edits (via the same Casbin store) are never overwritten.
/// </summary>
public static class CasbinPolicySeeder
{
    private static readonly (string Role, string Object, string Action)[] DefaultPolicies =
    [
        // Enumerated explicitly (no "*" wildcard): the matcher in rbac_model.conf does a
        // literal string comparison, so a wildcard row would silently never match anything.
        ("SuperAdmin", "alertas", "read"),
        ("SuperAdmin", "alertas", "write"),
        ("SuperAdmin", "camaras", "read"),
        ("SuperAdmin", "camaras", "write"),
        ("SuperAdmin", "blacklist", "read"),
        ("SuperAdmin", "blacklist", "write"),
        ("SuperAdmin", "usuarios", "read"),
        ("SuperAdmin", "usuarios", "write"),
        ("SuperAdmin", "lecturas-historicas", "read"),

        ("SupervisorC4", "alertas", "read"),
        ("SupervisorC4", "alertas", "write"),
        ("SupervisorC4", "camaras", "read"),
        ("SupervisorC4", "camaras", "write"),
        ("SupervisorC4", "blacklist", "read"),
        ("SupervisorC4", "blacklist", "write"),
        ("SupervisorC4", "usuarios", "read"),

        ("OperadorC4", "alertas", "read"),
        ("OperadorC4", "camaras", "read"),
        ("OperadorC4", "blacklist", "read"),

        ("PatrullaMovil", "alertas", "read"),

        ("AuditorForense", "alertas", "read"),
        ("AuditorForense", "camaras", "read"),
        ("AuditorForense", "blacklist", "read"),
        ("AuditorForense", "lecturas-historicas", "read"),
    ];

    public static void SeedDefaultPolicies(this IEnforcer enforcer)
    {
        foreach (var (role, obj, act) in DefaultPolicies)
        {
            if (!enforcer.HasPolicy(role, obj, act))
            {
                enforcer.AddPolicy(role, obj, act);
            }
        }
    }
}
