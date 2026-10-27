using Encore.Orders;
using Encore.Gigs;
using Xunit;

namespace Encore.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            GigId = Guid.NewGuid(),
            GigCode = "S-1",
            Reference = "FAN-1",
            Seats = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            PassNumber = "EN-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("EN-1", dto.PassNumber);
    }

    [Fact]
    public void GigDto_MapsTheScarceNumber()
    {
        var gig = new Gig { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Stage = "o", Band = "d", SeatsTotal = 10, SeatsLeft = 4, MaxPerFan = 3 };
        var dto = GigDto.From(gig);
        Assert.Equal(10, dto.SeatsTotal);
        Assert.Equal(3, dto.MaxPerFan);
        Assert.Equal(4, dto.SeatsLeft);
    }
}
