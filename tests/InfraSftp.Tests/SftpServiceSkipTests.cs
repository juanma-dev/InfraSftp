using InfraSftp.Services;

namespace InfraSftp.Tests;

// Exercises the rsync-style skip heuristic added in #1. Pure logic — no SSH
// connection is established. We construct an SftpService just to reach the
// instance method (it depends on ForceTransferProvider, which is per-instance).
public class SftpServiceSkipTests
{
    private static SftpService MakeService(bool force = false)
    {
        var svc = new SftpService(new KnownHostsService());
        svc.ForceTransferProvider = () => force;
        return svc;
    }

    private static readonly DateTime T0 = new(2026, 04, 26, 12, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void Skip_When_Same_Size_And_Same_Mtime()
    {
        var svc = MakeService();
        Assert.True(svc.ShouldSkipTransfer(1024, T0, 1024, T0));
    }

    [Fact]
    public void Transfer_When_Sizes_Differ()
    {
        var svc = MakeService();
        Assert.False(svc.ShouldSkipTransfer(1024, T0, 2048, T0));
    }

    [Fact]
    public void Transfer_When_Same_Size_But_Different_Content_Different_Mtime()
    {
        // The class of bug the fix targets: identical size, different content
        // — the destination is older (or newer) by more than the tolerance.
        var svc = MakeService();
        var newer = T0.AddSeconds(30);
        Assert.False(svc.ShouldSkipTransfer(1024, newer, 1024, T0));
    }

    [Theory]
    [InlineData(0)]      // identical
    [InlineData(1)]      // 1s — within window
    [InlineData(2)]      // exactly the boundary
    public void Skip_When_Mtime_Within_2s_Tolerance(int deltaSeconds)
    {
        var svc = MakeService();
        var dst = T0.AddSeconds(deltaSeconds);
        Assert.True(svc.ShouldSkipTransfer(1024, T0, 1024, dst));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(60)]
    [InlineData(86400)]
    public void Transfer_When_Mtime_Exceeds_Tolerance(int deltaSeconds)
    {
        var svc = MakeService();
        var dst = T0.AddSeconds(deltaSeconds);
        Assert.False(svc.ShouldSkipTransfer(1024, T0, 1024, dst));
    }

    [Fact]
    public void Tolerance_Is_Symmetric_Around_Source_Mtime()
    {
        // ±2s, not just +2s.
        var svc = MakeService();
        var earlier = T0.AddSeconds(-2);
        Assert.True(svc.ShouldSkipTransfer(1024, T0, 1024, earlier));
    }

    [Fact]
    public void Force_Transfer_Disables_Skip_Even_When_Match()
    {
        var svc = MakeService(force: true);
        Assert.False(svc.ShouldSkipTransfer(1024, T0, 1024, T0));
    }

    [Fact]
    public void Default_Mtime_Falls_Back_To_Size_Only_Skip()
    {
        // SETSTAT-denied servers may return mtime == default. We don't want to
        // re-transfer every file forever in that case — fall back to size.
        var svc = MakeService();
        Assert.True(svc.ShouldSkipTransfer(1024, default, 1024, default));
        Assert.False(svc.ShouldSkipTransfer(1024, default, 2048, default));
    }
}
