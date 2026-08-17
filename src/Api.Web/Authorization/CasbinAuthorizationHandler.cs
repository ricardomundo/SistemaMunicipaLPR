using Casbin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Api.Web.Authorization;

/// <summary>
/// Evaluates the "Casbin" policy: Keycloak is the source of truth for which roles a user
/// has (via the "role" claims populated from the token's realm_access.roles in Program.cs);
/// Casbin is the source of truth for which (obj, act) pairs each role may perform.
/// Access is granted if ANY of the user's roles is allowed by the enforcer.
/// </summary>
public sealed class CasbinAuthorizationHandler(IEnforcer enforcer, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<CasbinRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CasbinRequirement requirement)
    {
        var resource = httpContextAccessor.HttpContext?.GetEndpoint()?.Metadata.GetMetadata<CasbinResourceAttribute>();
        if (resource is null)
        {
            // No [CasbinResource] declared on the endpoint: fail closed rather than guess.
            return Task.CompletedTask;
        }

        var roles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value);

        if (roles.Any(role => enforcer.Enforce(role, resource.Object, resource.Action)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
