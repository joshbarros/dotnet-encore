using Encore.Shared;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Encore.Passes;

public enum PassStatus { Issued, Rejected }

public class Pass
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public PassStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PassesDbContext(DbContextOptions<PassesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "passes";

    public DbSet<Pass> Passes => Set<Pass>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Pass>(e =>
        {
            e.ToTable("Passes");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the pass once the seats are held. A poison reference is committed as rejected.</summary>
public class PassesHandler(PassesDbContext db) : IMessageHandler
{
    public const string Name = "passes";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Passes does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<SeatsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, SeatsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Passes.Add(new Pass
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = PassStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("passes", Topology.ReleaseKey, held.OrderId, new ReleaseSeats(held.OrderId, "poison"));
            db.Enqueue("passes", Topology.RejectedKey, held.OrderId, new PassRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"EN-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Passes.Add(new Pass
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = PassStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("passes", Topology.IssuedKey, held.OrderId, new PassIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
