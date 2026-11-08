using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class EventBusTests
{
    private sealed record Ping(int Value);
    private sealed record Pong(string Text);

    [Fact]
    public void Publish_DeliversToSubscriber()
    {
        var bus = new EventBus(null);
        var received = new List<int>();
        using var sub = bus.Subscribe<Ping>(p => received.Add(p.Value));

        bus.Publish(new Ping(1));
        bus.Publish(new Ping(2));

        Assert.Equal(new[] { 1, 2 }, received);
    }

    [Fact]
    public void Publish_OnlyDeliversExactType()
    {
        var bus = new EventBus(null);
        var pings = 0;
        var pongs = 0;
        using var a = bus.Subscribe<Ping>(_ => pings++);
        using var b = bus.Subscribe<Pong>(_ => pongs++);

        bus.Publish(new Ping(1));

        Assert.Equal(1, pings);
        Assert.Equal(0, pongs);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new EventBus(null);
        var count = 0;
        var sub = bus.Subscribe<Ping>(_ => count++);

        bus.Publish(new Ping(1));
        sub.Dispose();
        bus.Publish(new Ping(2));
        sub.Dispose(); // idempotent

        Assert.Equal(1, count);
    }

    [Fact]
    public void OnUiThreadSubscriber_WithoutDispatcher_RunsSynchronously()
    {
        var bus = new EventBus(null);
        var count = 0;
        using var sub = bus.Subscribe<Ping>(_ => count++, onUiThread: true);

        bus.Publish(new Ping(5));

        Assert.Equal(1, count);
    }

    [Fact]
    public void GetLast_ReturnsMostRecentEvent_OrNull()
    {
        var bus = new EventBus(null);
        Assert.Null(bus.GetLast<Ping>());

        bus.Publish(new Ping(1));
        bus.Publish(new Ping(7));

        Assert.Equal(7, bus.GetLast<Ping>()!.Value);
        Assert.Null(bus.GetLast<Pong>());
    }

    [Fact]
    public void GetLast_WorksWithoutSubscribers()
    {
        var bus = new EventBus(null);
        bus.Publish(new OpenSettingsRequested());
        Assert.NotNull(bus.GetLast<OpenSettingsRequested>());
    }

    [Fact]
    public void HandlerException_DoesNotAffectOtherHandlersOrPublisher()
    {
        var bus = new EventBus(null);
        var secondCalled = false;
        using var a = bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        using var b = bus.Subscribe<Ping>(_ => secondCalled = true);

        var ex = Record.Exception(() => bus.Publish(new Ping(1)));

        Assert.Null(ex);
        Assert.True(secondCalled);
    }

    [Fact]
    public void SubscriberDisposedDuringPublish_IsSkipped()
    {
        var bus = new EventBus(null);
        IDisposable? second = null;
        var secondCount = 0;
        using var first = bus.Subscribe<Ping>(_ => second?.Dispose());
        second = bus.Subscribe<Ping>(_ => secondCount++);

        bus.Publish(new Ping(1));
        bus.Publish(new Ping(2));

        Assert.Equal(0, secondCount);
    }

    [Fact]
    public void SubscriptionBag_DisposesAll()
    {
        var bus = new EventBus(null);
        var count = 0;
        var bag = new SubscriptionBag();
        bag.Add(bus.Subscribe<Ping>(_ => count++));
        bag.Add(bus.Subscribe<Ping>(_ => count++));

        bus.Publish(new Ping(1));
        bag.Dispose();
        bus.Publish(new Ping(2));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ConcurrentPublish_DeliversEveryEvent()
    {
        var bus = new EventBus(null);
        var count = 0;
        using var sub = bus.Subscribe<Ping>(_ => Interlocked.Increment(ref count));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) bus.Publish(new Ping(i));
        })));

        Assert.Equal(8 * 500, Volatile.Read(ref count));
    }
}
