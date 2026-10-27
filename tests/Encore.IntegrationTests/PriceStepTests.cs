using Encore.Gigs;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Encore.IntegrationTests;

public class PriceStepTests
{
    private static async Task<ReserveOutcome> ReserveAsync(Gig gig, int seats, Guid? owner = null)
    {
        await using var db = TestDatabase.Gigs();
        return await new GigsHandler(db).ReserveAsync(Guid.CreateVersion7(),
            new ReserveSeats(Guid.CreateVersion7(), owner ?? Guid.NewGuid(), gig.Id, seats, "FAN-PRICE"));
    }

    [Fact]
    public async Task EachHoldRecordsThePriceOfItsPlaceInTheLine()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 8, priceCents: 5_000);
        foreach (var seats in new[] { 1, 2, 3, 2 }) // places 0 | 1-2 | 3-5 | 6-7
            await ReserveAsync(gig, seats);

        await using var check = TestDatabase.Gigs();
        var holds = await check.Holds.OrderBy(h => h.CreatedAt).ToListAsync();
        Assert.Equal(new long[] { 4_000, 4_000 + 5_000, 15_000, 12_500 }, holds.Select(h => h.SubtotalCents).ToArray());
        var announced = await check.Outbox.Where(o => o.RoutingKey == Topology.HeldKey).ToListAsync();
        Assert.All(announced, m => Assert.Contains("subtotalCents", m.Payload));
    }

    [Fact]
    public async Task ConcurrentOrders_GetDisjointPlaces_SoTheTotalIsExactlyTheWholePriceLadder()
    {
        for (var round = 0; round < 3; round++)
        {
            await TestDatabase.OpenAsync();
            var gig = await TestDatabase.AddGigAsync(seatsTotal: 20, priceCents: 5_000, maxPerFan: 20);

            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                await ReserveAsync(gig, 1);
            })).ToArray();
            gate.SetResult();
            await Task.WhenAll(tasks);

            await using var check = TestDatabase.Gigs();
            var prices = (await check.Holds.Select(h => h.SubtotalCents).ToListAsync()).OrderBy(p => p).ToArray();
            // 5 early (4000), 10 standard (5000), 5 last (6250): every price used exactly as often as the ladder says
            var ladder = Enumerable.Repeat(4_000L, 5).Concat(Enumerable.Repeat(5_000L, 10)).Concat(Enumerable.Repeat(6_250L, 5)).ToArray();
            Assert.Equal(ladder, prices);
            Assert.Equal(0, await check.Gigs.Where(g => g.Id == gig.Id).Select(g => g.SeatsLeft).SingleAsync());
        }
    }

    [Fact]
    public async Task SeatsThatComeBackAreSoldAgainAtThePriceOfTheirPlace()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(seatsTotal: 8, priceCents: 5_000);
        var first = Guid.CreateVersion7();
        await using (var db = TestDatabase.Gigs())
            await new GigsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(first, Guid.NewGuid(), gig.Id, 2, "FAN-A"));
        await ReserveAsync(gig, 2); // places 2-3, standard

        await using (var db = TestDatabase.Gigs())
            Assert.True(await new GigsHandler(db).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(first, "refund")));
        await ReserveAsync(gig, 2); // 2 are sold again, so these are places 2-3 again: standard, not early bird

        await using var check = TestDatabase.Gigs();
        var last = await check.Holds.Where(h => h.Status == HoldStatus.Held).OrderByDescending(h => h.CreatedAt).FirstAsync();
        Assert.Equal(10_000, last.SubtotalCents);
    }
}
