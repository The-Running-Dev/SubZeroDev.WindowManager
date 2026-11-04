using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// The test hook's window effects (S26.10, I38): hides, shows and closes only the model-only synthetic windows, by
/// changing what they report. A handle it does not own is never touched; the plan reports it stuck, so a test that
/// somehow names a real window leaves that window exactly as it was.
/// </summary>
internal sealed class SyntheticWindowEffects(SyntheticWindows windows) : IWindowEffects
{
    private readonly object _gate = new();
    private readonly HashSet<nint> _hidden = [];

    public Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan, CancellationToken ct)
    {
        var stuck = new List<WindowKey>();
        var gone = new List<WindowKey>();
        foreach (var planned in plan.Hide)
        {
            switch (Check(planned))
            {
                case Verdict.Gone:
                    gone.Add(planned.Key);
                    break;
                case Verdict.NotOurs:
                    stuck.Add(planned.Key);
                    break;
                default:
                    if (windows.Refuses(planned.Key.Handle))
                    {
                        stuck.Add(planned.Key);
                        break;
                    }

                    lock (_gate)
                    {
                        _hidden.Add(planned.Key.Handle);
                    }

                    windows.SetVisible(planned.Key.Handle, false);
                    break;
            }
        }

        foreach (var planned in plan.Show)
        {
            switch (Check(planned))
            {
                case Verdict.Gone:
                    gone.Add(planned.Key);
                    break;
                case Verdict.Ours:
                    lock (_gate)
                    {
                        _hidden.Remove(planned.Key.Handle);
                    }

                    windows.SetVisible(planned.Key.Handle, true);
                    break;
            }
        }

        return Task.FromResult(Outcome<EffectsResult>.Success(new EffectsResult(stuck, gone)));
    }

    public Task<Outcome> ShowAllAsync(CancellationToken ct)
    {
        nint[] hidden;
        lock (_gate)
        {
            hidden = [.. _hidden];
            _hidden.Clear();
        }

        foreach (var handle in hidden)
        {
            windows.SetVisible(handle, true);
        }

        return Task.FromResult(Outcome.Success);
    }

    public Task<Outcome> CloseAsync(PlannedWindow window, CancellationToken ct)
    {
        switch (Check(window))
        {
            case Verdict.Gone:
                return Task.FromResult(Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone."));
            case Verdict.NotOurs:
                return Task.FromResult(Outcome.Fail(CoreErrorCode.WindowNotMovable, "The test hook closes only its own windows."));
            default:
                lock (_gate)
                {
                    _hidden.Remove(window.Key.Handle);
                }

                windows.Close(window.Key.Handle);
                return Task.FromResult(Outcome.Success);
        }
    }

    private Verdict Check(PlannedWindow planned)
    {
        if (!windows.Owns(planned.Key.Handle))
        {
            return windows.Read(planned.Key.Handle) is null ? Verdict.Gone : Verdict.NotOurs;
        }

        var facts = windows.Read(planned.Key.Handle);
        return facts is not null && facts.Fingerprint == planned.Fingerprint ? Verdict.Ours : Verdict.Gone;
    }

    private enum Verdict
    {
        Ours,
        NotOurs,
        Gone,
    }
}
