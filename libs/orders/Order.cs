namespace Encore.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid GigId { get; set; }
    public string GigCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Seats { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? PassNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SeatsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? GigCode, string? Reference, long Seats);

public record OrderDto(
    Guid Id, Guid OwnerId, string GigCode, string Reference, int Seats, long TotalCents, string Status,
    string? Reason, string? PassNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SeatsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.GigCode, b.Reference, b.Seats, b.TotalCents, b.Status.ToString(),
        b.Reason, b.PassNumber, b.CreatedAt, b.UpdatedAt, b.SeatsReleasedAt);
}
