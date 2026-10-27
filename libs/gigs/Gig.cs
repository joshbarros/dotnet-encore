namespace Encore.Gigs;

public class Gig
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Band { get; set; } = "";
    public DateTimeOffset GigDate { get; set; }
    public long PriceCents { get; set; }
    public int SeatsTotal { get; set; }
    public int SeatsLeft { get; set; }
    /// <summary>The most seats one fan may hold in this gig at any time.</summary>
    public int MaxPerFan { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class SeatHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid GigId { get; set; }
    public int Seats { get; set; }
    /// <summary>What these seats cost at the moment they were held, before the service fee. Fixed for the life of the hold.</summary>
    public long SubtotalCents { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>How many seats one fan holds in one gig. Written only by the Gigs handler.</summary>
public class GigFan
{
    public Guid GigId { get; set; }
    public Guid OwnerId { get; set; }
    public int Seats { get; set; }
}
