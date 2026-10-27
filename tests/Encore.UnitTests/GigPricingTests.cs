using Encore.Gigs;
using Xunit;

namespace Encore.UnitTests;

public class GigPricingTests
{
    [Theory]
    [InlineData(8, 0, PriceTier.EarlyBird)]
    [InlineData(8, 1, PriceTier.EarlyBird)]
    [InlineData(8, 2, PriceTier.Standard)]
    [InlineData(8, 5, PriceTier.Standard)]
    [InlineData(8, 6, PriceTier.LastSeats)]
    [InlineData(8, 7, PriceTier.LastSeats)]
    [InlineData(3, 0, PriceTier.Standard)] // a quarter of 3 is 0 whole seats: no steps
    [InlineData(3, 2, PriceTier.Standard)]
    [InlineData(1, 0, PriceTier.Standard)]
    [InlineData(100, 24, PriceTier.EarlyBird)]
    [InlineData(100, 25, PriceTier.Standard)]
    [InlineData(100, 74, PriceTier.Standard)]
    [InlineData(100, 75, PriceTier.LastSeats)]
    public void TierOf_FollowsThePlaceInTheLine(int total, int sold, PriceTier expected) =>
        Assert.Equal(expected, GigPricing.TierOf(total, sold));

    [Fact]
    public void SeatPrice_IsEightyPercentListOrOneTwentyFivePercent_TruncatedToACent()
    {
        Assert.Equal(4_000, GigPricing.SeatPriceCents(5_000, PriceTier.EarlyBird));
        Assert.Equal(5_000, GigPricing.SeatPriceCents(5_000, PriceTier.Standard));
        Assert.Equal(6_250, GigPricing.SeatPriceCents(5_000, PriceTier.LastSeats));
        Assert.Equal(0, GigPricing.SeatPriceCents(1, PriceTier.EarlyBird)); // 0.8 cents truncates
        Assert.Equal(1, GigPricing.SeatPriceCents(1, PriceTier.LastSeats)); // 1.25 cents truncates
    }

    [Fact]
    public void AnOrderThatSpansAStep_PaysEachSeatItsOwnPrice()
    {
        // 8 seats, list 5000: seats 0-1 early (4000), 2-5 standard (5000), 6-7 last (6250)
        Assert.Equal(4_000 + 4_000, GigPricing.SubtotalCents(5_000, 8, 0, 2));
        Assert.Equal(4_000 + 5_000, GigPricing.SubtotalCents(5_000, 8, 1, 2)); // straddles early -> standard
        Assert.Equal(5_000 + 6_250, GigPricing.SubtotalCents(5_000, 8, 5, 2)); // straddles standard -> last
        Assert.Equal(4_000 * 2 + 5_000 * 4 + 6_250 * 2, GigPricing.SubtotalCents(5_000, 8, 0, 8));
        Assert.Equal(0, GigPricing.SubtotalCents(5_000, 8, 3, 0));
    }

    [Fact]
    public void SplittingAnOrder_NeverChangesWhatTheSameSeatsCost()
    {
        var whole = GigPricing.SubtotalCents(7_777, 40, 5, 20);
        var parts = GigPricing.SubtotalCents(7_777, 40, 5, 6) + GigPricing.SubtotalCents(7_777, 40, 11, 14);
        Assert.Equal(whole, parts);
    }

    [Fact]
    public void NextSeat_ReportsThePriceAndTier_OrNothingWhenSoldOut()
    {
        Assert.Equal((4_000L, PriceTier.EarlyBird), GigPricing.NextSeat(5_000, 8, 8));
        Assert.Equal((5_000L, PriceTier.Standard), GigPricing.NextSeat(5_000, 8, 6));
        Assert.Equal((6_250L, PriceTier.LastSeats), GigPricing.NextSeat(5_000, 8, 2));
        Assert.Null(GigPricing.NextSeat(5_000, 8, 0));
    }

    [Fact]
    public void Overflow_ThrowsInsteadOfWrapping() =>
        Assert.Throws<OverflowException>(() => GigPricing.SeatPriceCents(long.MaxValue, PriceTier.LastSeats));
}
