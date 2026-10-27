using Encore.Orders;
using Encore.Gigs;
using Encore.Passes;
using Encore.Shared.Auth;
using Encore.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Encore.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ENCORE_TEST_DB")
        ?? "Host=localhost;Port=5485;Database=encore_test;Username=encore;Password=encore";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static GigsDbContext Gigs() => new(Options<GigsDbContext>(GigsDbContext.SchemaName));
    public static PassesDbContext Passes() => new(Options<PassesDbContext>(PassesDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var seats = Gigs();
        await using var passes = Passes();
        await orders.Database.MigrateAsync();
        await seats.Database.MigrateAsync();
        await passes.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "gigs.\"InboxMessages\", gigs.\"OutboxMessages\", gigs.\"SeatHolds\", gigs.\"GigFans\", gigs.\"Gigs\", " +
            "passes.\"InboxMessages\", passes.\"OutboxMessages\", passes.\"Passes\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Gig> AddGigAsync(int seatsTotal, long priceCents = 5_000, int? maxPerFan = null)
    {
        await using var db = Gigs();
        var gig = new Gig
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Gig",
            Stage = "Main Hall",
            Band = "The Blue Band",
            GigDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            SeatsTotal = seatsTotal,
            SeatsLeft = seatsTotal,
            MaxPerFan = maxPerFan ?? seatsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Gigs.Add(gig);
        await db.SaveChangesAsync();
        return gig;
    }

    public static async Task<Order> AddOrderAsync(Gig gig, string reference, int seats, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            GigId = gig.Id,
            GigCode = gig.Code,
            Reference = reference,
            Seats = seats,
            TotalCents = OrderRules.TotalCents(gig.PriceCents, seats),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Gig[] gigs) : IGigCatalog
{
    public Task<GigInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(gigs.Where(s => s.Code == code).Select(s => new GigInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
