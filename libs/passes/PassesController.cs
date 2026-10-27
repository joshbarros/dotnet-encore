using Encore.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Encore.Passes;

public record PassDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/passes")]
[Authorize]
public class PassesController(PassesDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<PassDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.Passes.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new PassDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
