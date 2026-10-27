using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Encore.Gigs;

public enum ReserveOutcome { Held, Short, CapReached, Duplicate }

/// <summary>
/// The only writer of Gig.SeatsLeft and of seat holds.
/// Two atomic steps in one transaction: a per-fan cap upsert that only moves while the new total fits, then one
/// conditional UPDATE that matches only while the remaining Seats still covers the request.
/// </summary>
public class GigsHandler(GigsDbContext db) : IMessageHandler
{
    public const string Name = "gigs";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveSeats>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseSeats>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Gigs does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveSeats command, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            // 1. the per-fan cap: one atomic upsert that only moves while the new total still fits
            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO gigs."GigFans" ("GigId", "OwnerId", "Seats")
                SELECT d."Id", {command.OwnerId}, {command.Seats} FROM gigs."Gigs" d
                WHERE d."Id" = {command.GigId} AND d."MaxPerFan" >= {command.Seats}
                ON CONFLICT ("GigId", "OwnerId") DO UPDATE SET "Seats" = gigs."GigFans"."Seats" + {command.Seats}
                WHERE gigs."GigFans"."Seats" + {command.Seats} <=
                      (SELECT "MaxPerFan" FROM gigs."Gigs" WHERE "Id" = {command.GigId})
                """, ct);

            // 2. the remaining seats: one conditional UPDATE, the only writer of SeatsLeft. It also returns how many seats
            //    were sold before this order, which fixes each seat's place in the price steps: two concurrent orders get
            //    disjoint ranges because the row lock serialises them.
            var taken = withinCap == 1
                ? await db.Database.SqlQuery<int>($"""
                    UPDATE gigs."Gigs" SET "SeatsLeft" = "SeatsLeft" - {command.Seats}
                    WHERE "Id" = {command.GigId} AND "SeatsLeft" >= {command.Seats}
                    RETURNING "SeatsTotal" - ("SeatsLeft" + {command.Seats}) AS "Value"
                    """).ToListAsync(ct)
                : [];

            if (withinCap == 1 && taken.Count == 1)
            {
                var priced = await db.Gigs.AsNoTracking().Where(s => s.Id == command.GigId)
                    .Select(s => new { s.PriceCents, s.SeatsTotal }).FirstAsync(ct);
                var subtotal = GigPricing.SubtotalCents(priced.PriceCents, priced.SeatsTotal, taken[0], command.Seats);
                db.Holds.Add(new SeatHold
                {
                    OrderId = command.OrderId,
                    OwnerId = command.OwnerId,
                    GigId = command.GigId,
                    Seats = command.Seats,
                    SubtotalCents = subtotal,
                    Status = HoldStatus.Held,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.Enqueue("gigs", Topology.HeldKey, command.OrderId,
                    new SeatsHeld(command.OrderId, command.OwnerId, command.GigId, command.Seats, command.Reference, subtotal));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // Either the cap or the stock said no. Roll back, so a cap increment never outlives a failed reserve.
            await tx.RollbackAsync(ct);
        }

        // The refusal is recorded in a fresh transaction (the claim was rolled back with the attempt).
        db.ChangeTracker.Clear();
        await using var refusal = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var gig = await db.Gigs.AsNoTracking().Where(s => s.Id == command.GigId)
            .Select(s => new { s.SeatsLeft, s.MaxPerFan }).FirstOrDefaultAsync(ct);
        var held = await db.Fans.AsNoTracking()
            .Where(c => c.GigId == command.GigId && c.OwnerId == command.OwnerId)
            .Select(c => c.Seats).FirstOrDefaultAsync(ct);
        var overCap = gig is not null && held + command.Seats > gig.MaxPerFan;
        db.Enqueue("gigs", Topology.ShortKey, command.OrderId,
            new SeatsShort(command.OrderId, command.Reference, command.Seats, gig?.SeatsLeft ?? 0,
                overCap ? "cap" : "stock", gig?.MaxPerFan ?? 0));
        await db.SaveChangesAsync(ct);
        await refusal.CommitAsync(ct);
        return overCap ? ReserveOutcome.CapReached : ReserveOutcome.Short;
    }

    /// <summary>Gives held seats back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseSeats command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Gigs
                    .Where(s => s.Id == hold.GigId && s.SeatsLeft + hold.Seats <= s.SeatsTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft + hold.Seats), ct);
                await db.Fans
                    .Where(c => c.GigId == hold.GigId && c.OwnerId == hold.OwnerId && c.Seats >= hold.Seats)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Seats, x => x.Seats - hold.Seats), ct);
                db.Enqueue("gigs", Topology.ReleasedKey, command.OrderId,
                    new SeatsReleased(command.OrderId, hold.Seats, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
