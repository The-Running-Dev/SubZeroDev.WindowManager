using SubZeroDev.WindowManager.Host.Platform;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class Win32EffectsTests
{
    private const uint HideFlag = 0x0080;
    private const uint ShowFlag = 0x0040;
    private const uint Quiet = 0x4017;

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private sealed class FakeNative : IWindowNative
    {
        public readonly List<(nint Window, uint Flags)> Calls = [];
        public readonly List<nint> Closed = [];
        public readonly HashSet<nint> Hung = [];
        public readonly HashSet<nint> Visible = [];
        public readonly HashSet<nint> Refuses = [];
        public readonly HashSet<int> Threads = [];
        public readonly Dictionary<nint, int> Errors = [];
        public long Clock;
        public long Waited;

        public bool IsHung(nint window) => Hung.Contains(window);

        public int SetPosition(nint window, uint flags)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Calls.Add((window, flags));
            if (Errors.TryGetValue(window, out var error))
            {
                return error;
            }

            if ((flags & HideFlag) != 0 && !Refuses.Contains(window))
            {
                Visible.Remove(window);
            }
            else if ((flags & ShowFlag) != 0)
            {
                Visible.Add(window);
            }

            return 0;
        }

        public bool IsVisible(nint window) => Visible.Contains(window);

        public int PostClose(nint window)
        {
            Closed.Add(window);
            return 0;
        }

        public long NowMs => Clock;

        public void Wait(int milliseconds)
        {
            Clock += milliseconds;
            Waited += milliseconds;
        }
    }

    private static WindowFingerprint Print(int n) => new(100 + n, 1000 + n, 200 + n, "Class" + n);

    private static PlannedWindow Planned(int n) => new(new WindowKey(n, n), Print(n));

    private static (Win32WindowEffects Effects, FakeNative Native, Dictionary<nint, WindowFingerprint> Live) Make(params int[] windows)
    {
        var live = windows.ToDictionary(n => (nint)n, Print);
        var native = new FakeNative();
        foreach (var n in windows)
        {
            native.Visible.Add(n);
        }

        return (new Win32WindowEffects(h => live.TryGetValue(h, out var p) ? p : null, native), native, live);
    }

    [Fact]
    public async Task A_window_whose_fingerprint_changed_is_gone_and_untouched()
    {
        var (effects, native, live) = Make(1, 2);
        using var _ = effects;
        live[1] = Print(1) with { ProcessStartTicks = 9 };
        live.Remove(2);

        var result = await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1)], [Planned(2)]), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(new[] { Planned(1).Key, Planned(2).Key }, result.Value!.Gone);
        Assert.Empty(result.Value.Stuck);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task Hides_and_shows_never_activate_move_size_or_block()
    {
        var (effects, native, _) = Make(1, 2);
        using var _e = effects;

        var result = await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1)], [Planned(2)]), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!.Stuck);
        Assert.Equal(new (nint, uint)[] { (1, Quiet | HideFlag), (2, Quiet | ShowFlag) }, native.Calls);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, native.Threads);
    }

    [Fact]
    public async Task Hung_refused_and_self_showing_windows_are_stuck_and_left_visible()
    {
        var (effects, native, _) = Make(1, 2, 3);
        using var _e = effects;
        native.Hung.Add(1);
        native.Errors[2] = 5;
        native.Refuses.Add(3);

        var result = await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1), Planned(2), Planned(3)], []), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(new[] { Planned(1).Key, Planned(2).Key, Planned(3).Key }, result.Value!.Stuck);
        Assert.DoesNotContain(native.Calls, c => c.Window == 1);
        Assert.Equal(ShowFlag, native.Calls.Last(c => c.Window == 3).Flags & ShowFlag);
        Assert.True(native.Waited <= Win32WindowEffects.VerifyBudgetMs);
        Assert.Contains((nint)3, native.Visible);
    }

    [Fact]
    public async Task Show_all_shows_only_what_this_instance_hid_and_still_matches()
    {
        var (effects, native, live) = Make(1, 2, 3);
        using var _e = effects;
        var ct = TestContext.Current.CancellationToken;
        await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1), Planned(2)], []), ct);
        live[2] = Print(2) with { ClassName = "Successor" };
        native.Calls.Clear();

        var shown = await effects.ShowAllAsync(ct);
        var again = await effects.ShowAllAsync(ct);

        Assert.True(shown.Ok);
        Assert.True(again.Ok);
        Assert.Equal(new (nint, uint)[] { (1, Quiet | ShowFlag) }, native.Calls);
    }

    [Fact]
    public async Task Close_posts_a_request_only_to_the_same_window()
    {
        var (effects, native, live) = Make(1, 2);
        using var _e = effects;
        live[2] = Print(2) with { ThreadId = 1 };
        var ct = TestContext.Current.CancellationToken;

        var closed = await effects.CloseAsync(Planned(1), ct);
        var gone = await effects.CloseAsync(Planned(2), ct);

        Assert.True(closed.Ok);
        Assert.Equal(CoreErrorCode.WindowGone, gone.Error!.Code);
        Assert.Equal(new nint[] { 1 }, native.Closed);
    }

    [Fact]
    public async Task A_cancelled_plan_touches_nothing()
    {
        var (effects, native, _) = Make(1);
        using var _e = effects;

        var result = await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1)], []), new CancellationToken(canceled: true));

        Assert.Equal(CoreErrorCode.Cancelled, result.Error!.Code);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task Work_after_dispose_completes_as_cancelled()
    {
        var (effects, native, _) = Make(1);
        effects.Dispose();

        var result = await effects.ApplyAsync(new SwitchPlan(A, B, [Planned(1)], []), TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.Cancelled, result.Error!.Code);
        Assert.Empty(native.Calls);
    }

    private sealed class FakeFacts(Dictionary<nint, WindowFingerprint> live) : IWindowFacts
    {
        public WindowFacts? Read(nint handle) => live.TryGetValue(handle, out var print)
            ? new WindowFacts(print, "", null, null, 0, "", true, CloakState.None, false, false, false, false, false, false)
            : null;
    }

    [Fact]
    public void A_thumbnail_of_a_stale_or_untracked_key_registers_nothing()
    {
        var live = new Dictionary<nint, WindowFingerprint> { [1] = Print(1) with { ThreadId = 7 } };
        var service = new Win32ThumbnailService(new FakeFacts(live), key => key.Handle == 1 ? Print(1) : null);

        Assert.Equal(CoreErrorCode.WindowGone, service.Register(Planned(1).Key, 0).Error!.Code);
        Assert.Equal(CoreErrorCode.WindowGone, service.Register(Planned(2).Key, 0).Error!.Code);
    }
}
