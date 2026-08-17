namespace Api.Web.Authorization;

/// <summary>
/// Declares the Casbin (obj, act) pair an endpoint requires. Roles that satisfy any of
/// them (as granted by a Casbin policy) are allowed through <see cref="CasbinAuthorizationHandler"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class CasbinResourceAttribute(string @object, string action) : Attribute
{
    public string Object { get; } = @object;
    public string Action { get; } = action;
}
