using Api.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.Controllers;

/// <summary>
/// Reference implementation of the auth wiring: [Authorize(Policy = "Casbin")] runs the
/// JWT check, then [CasbinResource] tells CasbinAuthorizationHandler which (obj, act) pair
/// to enforce for the caller's roles.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Casbin")]
public class BlacklistController : ControllerBase
{
    [HttpGet]
    [CasbinResource("blacklist", "read")]
    public IActionResult Get() => Ok(Array.Empty<object>());

    [HttpPost]
    [CasbinResource("blacklist", "write")]
    public IActionResult Post() => Accepted();
}
