namespace Encore.Gigs;

public enum PriceTier { EarlyBird, Standard, LastSeats }

/// <summary>
/// Seat prices step with how many seats have been sold: the first quarter of a gig sells at 80% of the list price,
/// the last quarter at 125%, the rest at the list price. The price belongs to the seat's place in the line, so an order
/// that spans a step pays each seat its own price.
/// </summary>
public static class GigPricing
{
    public const int EarlyBirdPercent = 80;
    public const int LastSeatsPercent = 125;

    /// <summary>How many seats (counted from the first sold) are in the early-bird step.</summary>
    public static int EarlyBirdSeats(int seatsTotal) => seatsTotal / 4;

    /// <summary>The index (0-based, among all seats sold) at which the last-seats step begins.</summary>
    public static int LastSeatsFrom(int seatsTotal) => seatsTotal - seatsTotal / 4;

    public static PriceTier TierOf(int seatsTotal, int soldIndex) =>
        soldIndex < EarlyBirdSeats(seatsTotal) ? PriceTier.EarlyBird
        : soldIndex >= LastSeatsFrom(seatsTotal) ? PriceTier.LastSeats
        : PriceTier.Standard;

    public static long SeatPriceCents(long listCents, PriceTier tier) => tier switch
    {
        PriceTier.EarlyBird => checked(listCents * EarlyBirdPercent / 100),
        PriceTier.LastSeats => checked(listCents * LastSeatsPercent / 100),
        _ => listCents,
    };

    /// <summary>What the seats numbered soldBefore .. soldBefore + seats - 1 cost together, before the service fee.</summary>
    public static long SubtotalCents(long listCents, int seatsTotal, int soldBefore, int seats)
    {
        long sum = 0;
        for (var i = soldBefore; i < soldBefore + seats; i++)
            sum = checked(sum + SeatPriceCents(listCents, TierOf(seatsTotal, i)));
        return sum;
    }

    /// <summary>The price the next seat sells at, given how many are left. Null when the gig is sold out.</summary>
    public static (long PriceCents, PriceTier Tier)? NextSeat(long listCents, int seatsTotal, int seatsLeft)
    {
        if (seatsLeft <= 0)
            return null;
        var tier = TierOf(seatsTotal, seatsTotal - seatsLeft);
        return (SeatPriceCents(listCents, tier), tier);
    }
}
