using Microsoft.AspNetCore.Authorization;

namespace Api.Web.Authorization;

/// <summary>
/// Marker requirement for the "Casbin" policy; the actual (obj, act) pair is read from
/// the endpoint's <see cref="CasbinResourceAttribute"/> by <see cref="CasbinAuthorizationHandler"/>.
/// </summary>
public sealed class CasbinRequirement : IAuthorizationRequirement;
