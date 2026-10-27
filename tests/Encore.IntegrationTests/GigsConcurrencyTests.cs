using System.Collections.Concurrent;
using Encore.Gigs;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Encore.IntegrationTests;

public class GigsConcurrencyTests
{
    [Fact]
    public async Task LastSeat_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Gigs();
            var outcome = await new GigsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(orderId, Guid.NewGuid(), gig.Id, 1, $"FAN-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Gigs();
        var remaining = await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Gigs())
            await new GigsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(orderId, Guid.NewGuid(), gig.Id, 3, "FAN-1"));

        // Two different commands (poison compensation and a refund) both ask for the same seats back.
        await using var first = TestDatabase.Gigs();
        await using var second = TestDatabase.Gigs();
        var one = await new GigsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(orderId, "poison"));
        var two = await new GigsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(orderId, "refund"));

        await using var check = TestDatabase.Gigs();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSeats(Guid.CreateVersion7(), Guid.NewGuid(), gig.Id, 2, "FAN-1");

        await using var a = TestDatabase.Gigs();
        await using var b = TestDatabase.Gigs();
        var first = await new GigsHandler(a).ReserveAsync(messageId, command);
        var second = await new GigsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Gigs();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 2);
        await using var db = TestDatabase.Gigs();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Gigs.Where(s => s.Id == gig.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft - 3)));
    }

    [Fact]
    public async Task Cap_ConcurrentOrdersByTheSameFan_NeverPassTheCap()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 20, maxPerFan: 2);
        var fan = Guid.NewGuid();

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Gigs();
            outcomes.Add(await new GigsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 1, $"FAN-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Gigs();
        Assert.Equal(2, outcomes.Count(o => o == ReserveOutcome.Held));
        Assert.Equal(4, outcomes.Count(o => o == ReserveOutcome.CapReached));
        Assert.Equal(2, await check.Fans.Where(c => c.OwnerId == fan).Select(c => c.Seats).SingleAsync());
        Assert.Equal(18, await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync());
        Assert.Equal(2, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task Cap_AtWholeRequestLevel_RefusesTheOrderThatWouldCrossIt()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 10, maxPerFan: 2);
        var fan = Guid.NewGuid();
        await using var db = TestDatabase.Gigs();
        var handler = new GigsHandler(db);

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 1, "FAN-1")));
        // 1 held + 2 asked = 3 > 2: nothing moves, neither the stock nor the fan's count
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 2, "FAN-2")));
        // another fan is unaffected
        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), Guid.NewGuid(), gig.Id, 2, "FAN-3")));

        await using var check = TestDatabase.Gigs();
        Assert.Equal(1, await check.Fans.Where(c => c.OwnerId == fan).Select(c => c.Seats).SingleAsync());
        Assert.Equal(7, await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync());
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync();
        Assert.Single(refusal);
        Assert.Contains("\"cap\"", refusal[0].Payload);
    }

    [Fact]
    public async Task Cap_StockShortAfterTheCapPassed_RollsTheCapBack()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 1, maxPerFan: 2);
        var fan = Guid.NewGuid();
        await using var db = TestDatabase.Gigs();

        var outcome = await new GigsHandler(db).ReserveAsync(
            Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 2, "FAN-1"));

        await using var check = TestDatabase.Gigs();
        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.False(await check.Fans.AnyAsync(c => c.OwnerId == fan));
        Assert.Equal(1, await check.Gigs.Where(s => s.Id == gig.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task Cap_ReleaseGivesTheFanTheirAllowanceBack()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 10, maxPerFan: 2);
        var fan = Guid.NewGuid();
        var first = Guid.CreateVersion7();
        await using var db = TestDatabase.Gigs();
        var handler = new GigsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(first, fan, gig.Id, 2, "FAN-1"));
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 1, "FAN-2")));

        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(first, "refund")));
        Assert.False(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(first, "refund again")));

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), fan, gig.Id, 2, "FAN-3")));
        await using var check = TestDatabase.Gigs();
        Assert.Equal(2, await check.Fans.Where(c => c.OwnerId == fan).Select(c => c.Seats).SingleAsync());
    }
}
