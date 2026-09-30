using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;

namespace ServiceDashboard.Controllers;

[ApiController]
[Route("api/health")]
[AllowAnonymous]
public sealed class HealthController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var dbOk = await db.Database.CanConnectAsync();
        return dbOk ? Ok(new { status = "ok" }) : StatusCode(503, new { status = "database unavailable" });
    }
}
