using Encore.Shared;
using Encore.Shared.Auth;
using Encore.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Encore.Gigs;

public record CreateGigRequest(string? Code, string? Name, string? Stage, string? Band, DateTimeOffset? GigDate, long SeatsTotal, long PriceCents, long? MaxPerFan = null);

public record GigDto(Guid Id, string Code, string Name, string Stage, string Band, DateTimeOffset GigDate, long PriceCents, int SeatsTotal, int SeatsLeft, int MaxPerFan, long? NextSeatPriceCents, string? Tier)
{
    public static GigDto From(Gig s) => new(s.Id, s.Code, s.Name, s.Stage, s.Band, s.GigDate, s.PriceCents, s.SeatsTotal, s.SeatsLeft, s.MaxPerFan,
        GigPricing.NextSeat(s.PriceCents, s.SeatsTotal, s.SeatsLeft)?.PriceCents, GigPricing.NextSeat(s.PriceCents, s.SeatsTotal, s.SeatsLeft)?.Tier.ToString());
}

/// <summary>What other modules may ask Gigs, over Gigs's own tables.</summary>
public sealed class GigCatalog(GigsDbContext db) : IGigCatalog
{
    public async Task<GigInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Gigs.AsNoTracking().Where(s => s.Code == code).Select(s => new GigInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/gigs")]
[Authorize]
public class GigsController(GigsDbContext db) : ControllerBase
{
    private const int MaxSeatsTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<GigDto>> List(CancellationToken ct) =>
        (await db.Gigs.AsNoTracking().OrderBy(s => s.GigDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(GigDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<GigDto>> Get(string code, CancellationToken ct)
    {
        var gig = await db.Gigs.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return gig is null ? NotFound() : GigDto.From(gig);
    }

    /// <summary>Opens a gig. Staff only. This is the only place SeatsLeft is initialised; after that only the Gigs handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<GigDto>> Create(CreateGigRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Stage) || string.IsNullOrWhiteSpace(request.Band))
            throw new DomainException(400, "Name, stage and band are required.");
        if (request.SeatsTotal < 1 || request.SeatsTotal > MaxSeatsTotal)
            throw new DomainException(400, $"Seats on sale must be between 1 and {MaxSeatsTotal} seats.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per seat.");
        var cap = request.MaxPerFan ?? Math.Min(4, request.SeatsTotal);
        if (cap < 1 || cap > request.SeatsTotal)
            throw new DomainException(400, "Per-fan cap must be at least 1 and no more than the seats on sale.");
        if (await db.Gigs.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Gig '{code}' already exists.");

        var gig = new Gig
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Stage = request.Stage.Trim(),
            Band = request.Band.Trim(),
            GigDate = request.GigDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            SeatsTotal = (int)request.SeatsTotal,
            SeatsLeft = (int)request.SeatsTotal,
            MaxPerFan = (int)cap,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Gigs.Add(gig);
        await db.SaveChangesAsync(ct);
        return Created($"/api/gigs/{gig.Code}", GigDto.From(gig));
    }
}
