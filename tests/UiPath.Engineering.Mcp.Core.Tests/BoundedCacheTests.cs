using UiPath.Engineering.Mcp.Core.Caching;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class BoundedCacheTests {
    [Fact]
    public async Task Set_OverMaxEntries_EvictsLruAndDropsIdleLocks() {
        using var cache = new BoundedCache<string>(maxEntries: 2, ttl: TimeSpan.FromHours(1));

        await cache.RunExclusiveAsync("a", _ => { cache.Set("a", "A"); return Task.FromResult(0); });
        await cache.RunExclusiveAsync("b", _ => { cache.Set("b", "B"); return Task.FromResult(0); });
        await cache.RunExclusiveAsync("c", _ => { cache.Set("c", "C"); return Task.FromResult(0); });

        Assert.Equal(2, cache.EntryCount);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out var b));
        Assert.Equal("B", b);
        Assert.True(cache.TryGet("c", out var c));
        Assert.Equal("C", c);
        Assert.Equal(2, cache.LockCount);
    }

    [Fact]
    public async Task TryGet_AfterTtl_MissesUnlessIncludeExpired() {
        var time = new ManualTimeProvider();
        using var cache = new BoundedCache<string>(maxEntries: 4, ttl: TimeSpan.FromMinutes(5), timeProvider: time);

        await cache.RunExclusiveAsync("k", _ => { cache.Set("k", "v"); return Task.FromResult(0); });
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(cache.TryGet("k", out var expired, includeExpired: true));
        Assert.Equal("v", expired);
        Assert.False(cache.TryGet("k", out _));
    }
}
