using Encore.Orders;
using Encore.Passes;
using Encore.Shared;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Encore.IntegrationTests;

public class SagaTests
{
    private static OrdersHandler Handler(OrdersDbContext db) => new(db, Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var order = await TestDatabase.AddOrderAsync(gig, "FAN-POISON-1", 2, OrderStatus.Held);
        var held = new SeatsHeld(order.Id, order.OwnerId, gig.Id, 2, order.Reference);

        await using var docs = TestDatabase.Passes();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new PassesHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Passes();
        var doc = await check.Passes.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(PassStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(order.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Refund_ThenFinalMark_LeavesOrderRefundedAndPassIssued()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(gig, "FAN-1", 2, OrderStatus.Held, owner);

        // The pass is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Passes())
            await new PassesHandler(docs).IssueAsync(Guid.CreateVersion7(), new SeatsHeld(order.Id, owner, gig.Id, 2, order.Reference));

        // The owner reverses first.
        await using (var orders = TestDatabase.Orders())
            await new OrderService(orders, new FixedCatalog(gig), new TestUser(owner)).RefundAsync(order.Id, default);

        await using var late = TestDatabase.Orders();
        var mark = await Handler(late).MarkSoldAsync(Guid.CreateVersion7(), new PassIssued(order.Id, "EN-TEST"));

        await using var check = TestDatabase.Orders();
        await using var checkDocs = TestDatabase.Passes();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Refunded, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(PassStatus.Issued, (await checkDocs.Passes.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var held = await TestDatabase.AddOrderAsync(gig, "FAN-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(gig, "FAN-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(gig, "FAN-C", 1, OrderStatus.Requested);

        await using (var a = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkSoldAsync(Guid.CreateVersion7(), new PassIssued(held.Id, "EN-1")));
        await using (var b = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkSoldAsync(Guid.CreateVersion7(), new PassIssued(rejected.Id, "EN-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Orders())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkSoldAsync(Guid.CreateVersion7(), new PassIssued(requested.Id, "EN-3")));

        await using var check = TestDatabase.Orders();
        var statuses = (await check.Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Sold, statuses["FAN-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["FAN-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["FAN-C"]);
    }

    [Fact]
    public async Task Refund_OfARejectedOrder_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(gig, "FAN-1", 1, OrderStatus.Rejected, owner);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(gig), new TestUser(owner)).RefundAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Refund_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var order = await TestDatabase.AddOrderAsync(gig, "FAN-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(gig), new TestUser(Guid.NewGuid())).RefundAsync(order.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Orders();
        Assert.Equal(OrderStatus.Held, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanRefundAnyonesOrder()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var order = await TestDatabase.AddOrderAsync(gig, "FAN-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var refunded = await new OrderService(db, new FixedCatalog(gig), new TestUser(Guid.NewGuid(), staff: true)).RefundAsync(order.Id, default);
        Assert.Equal(OrderStatus.Refunded, refunded.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Orders();
        var order = await new OrderService(db, new FixedCatalog(gig), new TestUser(owner))
            .CreateAsync(new CreateOrderRequest(gig.Code, "FAN-1", 2), default);

        await using var check = TestDatabase.Orders();
        Assert.Equal(owner, (await check.Orders.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveSeats>(message.Payload).OwnerId);
        Assert.Equal(OrderStatus.Requested, order.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var gig = await TestDatabase.AddGigAsync(5);
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(gig), new TestUser(null)).CreateAsync(new CreateOrderRequest(gig.Code, "FAN-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownGig_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateOrderRequest("NOPE", "FAN-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
