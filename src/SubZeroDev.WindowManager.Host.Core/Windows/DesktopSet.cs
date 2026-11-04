namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The shell's virtual desktops. A switch has one order: the journal is written first as a superset of what can be
/// hidden (I28), the hides and shows run on the effects thread (I27), the journal is then narrowed to what is hidden, and
/// one snapshot changes the current desktop and every window's facts together (I42). A window that closes meanwhile is
/// dropped from that snapshot; nothing is left in the hidden set, the journal or the model that names it.
/// </summary>
public sealed class DesktopSet : IDesktopSet, IDisposable
{
    private sealed record Pending(Guid Target, TaskCompletionSource<Outcome> Done);

    private readonly WindowModel _model;
    private readonly IWindowEffects _effects;
    private readonly IRecoveryJournal _journal;
    private readonly IForegroundService _foreground;
    private readonly DesktopStore _store;
    private readonly Action<CoreError>? _diagnostic;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<WindowKey> _reported = [];
    private bool _running;
    private Pending? _pending;

    /// <param name="model">The registry, which holds the desktops and which desktop each window is filed on.</param>
    /// <param name="effects">Hides and shows windows on its own thread.</param>
    /// <param name="journal">The recovery journal, written before a window is hidden.</param>
    /// <param name="foreground">Activates the window to land on after a switch.</param>
    /// <param name="store"><c>desktops.json</c>.</param>
    /// <param name="diagnostic">Told once per window that refuses to be hidden.</param>
    public DesktopSet(WindowModel model, IWindowEffects effects, IRecoveryJournal journal, IForegroundService foreground,
        DesktopStore store, Action<CoreError>? diagnostic = null)
    {
        _model = model;
        _effects = effects;
        _journal = journal;
        _foreground = foreground;
        _store = store;
        _diagnostic = diagnostic;
        var loaded = store.Load();
        Problem = loaded.Problem;

        // The shell always starts on the first desktop; the file's lastCurrent is kept for the next release.
        model.ConfigureDesktops(new DesktopsView(loaded.Desktops[0].Id, false, null, loaded.Desktops));
        model.SelfShown += OnSelfShown;
    }

    /// <summary>Why <c>desktops.json</c> was not used, or null; the host reports it as a diagnostic and a settings banner.</summary>
    public string? Problem { get; }

    public IReadOnlyList<DesktopInfo> Desktops => _model.Desktops.Desktops;

    public Guid CurrentId => _model.Desktops.Current;

    public event EventHandler? Changed;

    public void Dispose() => _model.SelfShown -= OnSelfShown;

    public Outcome<Guid> Create(string? name)
    {
        var info = new DesktopInfo(Guid.NewGuid(), Clean(name));
        _model.SetDesktops(v => v with { Desktops = [.. v.Desktops, info] });
        Save();
        RaiseChanged();
        return Outcome<Guid>.Success(info.Id);
    }

    public Outcome Rename(Guid id, string name)
    {
        if (!Exists(id))
        {
            return NotFound(id);
        }

        var clean = Clean(name);
        _model.SetDesktops(v => v with { Desktops = [.. v.Desktops.Select(d => d.Id == id ? d with { Name = clean } : d)] });
        Save();
        RaiseChanged();
        return Outcome.Success;
    }

    public Outcome<SwitchPlan> PlanSwitch(Guid id) =>
        Exists(id) ? Outcome<SwitchPlan>.Success(_model.PlanSwitch(id)) : Outcome<SwitchPlan>.Fail(NotFound(id).Error!);

    public async Task<Outcome> SwitchAsync(Guid id, CancellationToken ct)
    {
        if (!Exists(id))
        {
            return NotFound(id);
        }

        Pending? held = null;
        lock (_gate)
        {
            if (_running)
            {
                _pending?.Done.TrySetResult(Outcome.Fail(CoreErrorCode.SwitchSuperseded, "A later switch replaced this one before it started."));
                held = _pending = new Pending(id, new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            else
            {
                _running = true;
            }
        }

        if (held is not null)
        {
            return await held.Done.Task.ConfigureAwait(false);
        }

        var result = await RunGuardedAsync(id).ConfigureAwait(false);
        while (true)
        {
            Pending? next;
            lock (_gate)
            {
                next = _pending;
                _pending = null;
                if (next is null)
                {
                    _running = false;
                }
            }

            if (next is null)
            {
                return result;
            }

            next.Done.TrySetResult(await RunGuardedAsync(next.Target).ConfigureAwait(false));
        }
    }

    public async Task<Outcome> MoveAsync(WindowKey key, Guid id, CancellationToken ct) =>
        (await MoveCoreAsync(key, id).ConfigureAwait(false)).Result;

    public async Task<Outcome<Guid>> MoveToNewAsync(WindowKey key, CancellationToken ct)
    {
        if (_model.Lookup(key) is null)
        {
            return Outcome<Guid>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
        }

        var created = Create(null).Value;
        var (result, refiled) = await MoveCoreAsync(key, created).ConfigureAwait(false);
        if (result.Ok)
        {
            return Outcome<Guid>.Success(created);
        }

        if (!refiled)
        {
            // Nothing was filed on it: the desktop made for this call goes with the call.
            await RemoveAsync(created, CancellationToken.None).ConfigureAwait(false);
        }

        return Outcome<Guid>.Fail(result.Error!);
    }

    public async Task<Outcome> RemoveAsync(Guid id, CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var view = _model.Desktops;
            var index = view.Desktops.ToList().FindIndex(d => d.Id == id);
            if (index < 0)
            {
                return NotFound(id);
            }

            if (view.Desktops.Count == 1)
            {
                return Outcome.Fail(CoreErrorCode.LastDesktop, "The only desktop cannot be removed.");
            }

            // Its windows move to the previous desktop, or the next when it was the first.
            var destination = view.Desktops[index > 0 ? index - 1 : 1].Id;
            var current = view.Current == id ? destination : view.Current;
            var show = current == destination ? _model.HiddenOn([id, destination]) : [];
            var result = new EffectsResult([], []);
            if (show.Count > 0)
            {
                var applied = await ApplyAsync(new SwitchPlan(view.Current, current, [], show)).ConfigureAwait(false);
                if (!applied.Ok)
                {
                    return Outcome.Fail(applied.Error!.Code, applied.Error.Detail, applied.Error.Retryable);
                }

                result = applied.Value!;
            }

            _model.CommitDesktops(
                current,
                [],
                [.. show.Select(p => p.Key).Except(result.Gone).Except(result.NotShown)],
                result.Stuck,
                result.Gone,
                v => v with { Desktops = [.. v.Desktops.Where(d => d.Id != id)] },
                (id, destination));
            Report(result.Stuck);
            await WriteExactJournalAsync().ConfigureAwait(false);
            Save();
            RaiseChanged();
            if (current != view.Current)
            {
                await ActivateLastAsync(current).ConfigureAwait(false);
            }

            return Outcome.Success;
        }
        finally
        {
            _exclusive.Release();
        }
    }

    public async Task<Outcome> PinToAllAsync(WindowKey key, bool pinned, CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_model.Lookup(key) is not { } window)
            {
                return Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone.");
            }

            if (window.Pinned == pinned)
            {
                return Outcome.Success;
            }

            if (!pinned)
            {
                // Back on one desktop: the one the user is on, where it already is.
                _model.FileOn(key, _model.Desktops.Current, pinned: false);
                RaiseChanged();
                return Outcome.Success;
            }

            _model.FileOn(key, window.Desktop, pinned: true);
            if (!window.ShellHidden)
            {
                RaiseChanged();
                return Outcome.Success;
            }

            var applied = await ApplyAsync(new SwitchPlan(window.Desktop, _model.Desktops.Current, [], [window.Planned])).ConfigureAwait(false);
            if (!applied.Ok)
            {
                _model.FileOn(key, window.Desktop, pinned: false);
                return Outcome.Fail(applied.Error!.Code, applied.Error.Detail, applied.Error.Retryable);
            }

            var result = applied.Value!;
            var notShown = result.NotShown.Contains(key);
            _model.CommitDesktops(_model.Desktops.Current, [], result.Gone.Contains(key) || notShown ? [] : [key], result.Stuck, result.Gone);
            await WriteExactJournalAsync().ConfigureAwait(false);
            RaiseChanged();
            if (notShown)
            {
                return Outcome.Fail(CoreErrorCode.CommandFailed, "The window would not be shown.", retryable: true);
            }

            return result.Gone.Contains(key) ? Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone.") : Outcome.Success;
        }
        finally
        {
            _exclusive.Release();
        }
    }

    public async Task<Outcome> RevealAsync(WindowKey key, CancellationToken ct)
    {
        if (_model.Lookup(key) is not { } window)
        {
            return Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone.");
        }

        return window.Pinned || window.Desktop == _model.Desktops.Current ? Outcome.Success : await SwitchAsync(window.Desktop, ct).ConfigureAwait(false);
    }

    /// <summary>Shows every window the shell hid and empties the journal entry. The shell is closing.</summary>
    public async Task RestoreAllAsync()
    {
        await _exclusive.WaitAsync().ConfigureAwait(false);
        try
        {
            var hidden = _model.HiddenWindows();
            if (hidden.Count == 0)
            {
                return;
            }

            _model.BeginEffects();
            try
            {
                var shown = await _effects.ShowAllAsync(CancellationToken.None).ConfigureAwait(false);
                if (!shown.Ok)
                {
                    // Some stayed hidden: the journal keeps naming them, so the guardian can still show them.
                    _diagnostic?.Invoke(new CoreError(CoreErrorCode.CommandFailed, "Some hidden windows could not be shown on exit: " + shown.Error!.Detail, true));
                    return;
                }

                _model.CommitDesktops(_model.Desktops.Current, [], [.. hidden.Select(p => p.Key)], [], []);
            }
            finally
            {
                _model.EndEffects();
            }

            await WriteExactJournalAsync().ConfigureAwait(false);
            RaiseChanged();
        }
        finally
        {
            _exclusive.Release();
        }
    }

    private async Task<Outcome> RunGuardedAsync(Guid target)
    {
        try
        {
            return await RunSwitchAsync(target).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ClearBusy();
            return Outcome.Fail(CoreErrorCode.CommandFailed, e.Message);
        }
    }

    private async Task<Outcome> RunSwitchAsync(Guid target)
    {
        await _exclusive.WaitAsync().ConfigureAwait(false);
        try
        {
            var from = _model.Desktops.Current;
            if (from == target)
            {
                return Outcome.Success;
            }

            if (!Exists(target))
            {
                return NotFound(target);
            }

            _model.SetDesktops(v => v with { Busy = true, Switching = new DesktopSwitching(from, target) });
            var plan = _model.PlanSwitch(target);
            var applied = await ApplyAsync(plan).ConfigureAwait(false);
            if (!applied.Ok)
            {
                ClearBusy();
                await WriteExactJournalAsync().ConfigureAwait(false);
                return Outcome.Fail(applied.Error!.Code, applied.Error.Detail, applied.Error.Retryable);
            }

            var result = applied.Value!;
            _model.BeginEffects();
            try
            {
                _model.CommitDesktops(
                    target,
                    [.. plan.Hide.Select(p => p.Key).Except(result.Stuck).Except(result.Gone)],
                    [.. plan.Show.Select(p => p.Key).Except(result.Gone).Except(result.NotShown)],
                    result.Stuck,
                    result.Gone);
            }
            finally
            {
                _model.EndEffects();
            }

            Report(result.Stuck);
            await WriteExactJournalAsync().ConfigureAwait(false);
            Save();
            RaiseChanged();
            await ActivateLastAsync(target).ConfigureAwait(false);
            return Outcome.Success;
        }
        finally
        {
            _exclusive.Release();
        }
    }

    private async Task<(Outcome Result, bool Refiled)> MoveCoreAsync(WindowKey key, Guid desktop)
    {
        await _exclusive.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Exists(desktop))
            {
                return (NotFound(desktop), false);
            }

            if (_model.Lookup(key) is not { } window)
            {
                return (Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone."), false);
            }

            var current = _model.Desktops.Current;
            var hide = desktop != current && !window.ShellHidden && window.Visible;
            var show = desktop == current && window.ShellHidden;
            if (hide && window.Stuck)
            {
                return (Outcome.Fail(CoreErrorCode.WindowNotMovable, "The window refuses to be hidden."), false);
            }

            var plan = new SwitchPlan(current, desktop, hide ? [window.Planned] : [], show ? [window.Planned] : []);

            // The journal names the window before it is filed away or hidden.
            if (hide)
            {
                var written = await WriteJournalAsync(_model.HiddenWindows().Append(window.Planned).DistinctBy(p => p.Key)).ConfigureAwait(false);
                if (!written.Ok)
                {
                    return (written, false);
                }
            }

            if (!_model.FileOn(key, desktop, pinned: false))
            {
                await WriteExactJournalAsync().ConfigureAwait(false);
                return (Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone."), false);
            }

            if (!hide && !show)
            {
                RaiseChanged();
                return (Outcome.Success, true);
            }

            _model.BeginEffects();
            try
            {
                var applied = await _effects.ApplyAsync(plan, CancellationToken.None).ConfigureAwait(false);
                if (!applied.Ok)
                {
                    if (hide)
                    {
                        // The hide may have landed before the effects failed: the window must not stay hidden and refiled back.
                        await _effects.ApplyAsync(new SwitchPlan(desktop, current, [], [window.Planned]), CancellationToken.None).ConfigureAwait(false);
                    }

                    _model.FileOn(key, window.Desktop, window.Pinned);
                    await WriteExactJournalAsync().ConfigureAwait(false);
                    return (Outcome.Fail(applied.Error!.Code, applied.Error.Detail, applied.Error.Retryable), false);
                }

                var result = applied.Value!;
                if (result.NotShown.Contains(key))
                {
                    _model.FileOn(key, window.Desktop, window.Pinned);
                    await WriteExactJournalAsync().ConfigureAwait(false);
                    RaiseChanged();
                    return (Outcome.Fail(CoreErrorCode.CommandFailed, "The window would not be shown.", retryable: true), false);
                }

                if (result.Gone.Contains(key))
                {
                    _model.CommitDesktops(current, [], [], [], result.Gone);
                    await WriteExactJournalAsync().ConfigureAwait(false);
                    RaiseChanged();
                    return (Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone."), true);
                }

                if (result.Stuck.Contains(key))
                {
                    _model.CommitDesktops(current, [], [], result.Stuck, result.Gone);
                    Report(result.Stuck);
                    await WriteExactJournalAsync().ConfigureAwait(false);
                    RaiseChanged();
                    return (Outcome.Fail(CoreErrorCode.WindowNotMovable, "The window refuses to be hidden."), false);
                }

                _model.CommitDesktops(current, hide ? [key] : [], show ? [key] : [], [], result.Gone);
            }
            finally
            {
                _model.EndEffects();
            }

            await WriteExactJournalAsync().ConfigureAwait(false);
            RaiseChanged();
            return (Outcome.Success, true);
        }
        finally
        {
            _exclusive.Release();
        }
    }

    /// <summary>A window the shell hid showed itself: hide it again, unless the hidden set already let it go.</summary>
    private void OnSelfShown(WindowKey key) => _ = RehideAsync(key);

    private async Task RehideAsync(WindowKey key)
    {
        try
        {
            await _exclusive.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_model.Lookup(key) is not { ShellHidden: true, Stuck: false } window)
                {
                    return;
                }

                var current = _model.Desktops.Current;
                _model.BeginEffects();
                try
                {
                    var applied = await _effects.ApplyAsync(new SwitchPlan(current, current, [window.Planned], []), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!applied.Ok)
                    {
                        return;
                    }

                    var result = applied.Value!;
                    _model.CommitDesktops(
                        current,
                        result.Stuck.Contains(key) || result.Gone.Contains(key) ? [] : [key],
                        [],
                        result.Stuck,
                        result.Gone);
                    Report(result.Stuck);
                }
                finally
                {
                    _model.EndEffects();
                }

                await WriteExactJournalAsync().ConfigureAwait(false);
            }
            finally
            {
                _exclusive.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _diagnostic?.Invoke(new CoreError(CoreErrorCode.CommandFailed, "Hiding a window again failed: " + e.Message, true));
        }
    }

    /// <summary>
    /// Writes the superset the plan could hide, then applies it. If the journal cannot be written the plan is not applied,
    /// and if the effects fail the hides are undone.
    /// </summary>
    private async Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan)
    {
        if (plan.Hide.Count > 0)
        {
            var written = await WriteJournalAsync(_model.HiddenWindows().Concat(plan.Hide).DistinctBy(p => p.Key)).ConfigureAwait(false);
            if (!written.Ok)
            {
                return Outcome<EffectsResult>.Fail(written.Error!);
            }
        }

        _model.BeginEffects();
        try
        {
            var applied = await _effects.ApplyAsync(plan, CancellationToken.None).ConfigureAwait(false);
            if (!applied.Ok && plan.Hide.Count > 0)
            {
                await _effects.ApplyAsync(new SwitchPlan(plan.To, plan.From, [], plan.Hide), CancellationToken.None).ConfigureAwait(false);
            }

            return applied;
        }
        finally
        {
            _model.EndEffects();
        }
    }

    private async Task<Outcome> WriteJournalAsync(IEnumerable<PlannedWindow> windows)
    {
        var hidden = windows
            .Select(w => new Recovery.HiddenWindow(w.Key.Handle, w.Fingerprint.ProcessId, w.Fingerprint.ProcessStartTicks, w.Fingerprint.ThreadId, w.Fingerprint.ClassName))
            .ToList();
        var change = hidden.Count == 0 ? null : Recovery.DesktopChange.HiddenWindows(hidden, DateTimeOffset.UtcNow);
        var written = await _journal.ReplaceAsync(Recovery.DesktopChangeKind.HiddenWindows, change, CancellationToken.None).ConfigureAwait(false);
        return written.Ok
            ? Outcome.Success
            : Outcome.Fail(CoreErrorCode.JournalWriteFailed, written.Error!.Detail, written.Error.Retryable);
    }

    /// <summary>Narrows the journal to the windows that really are hidden. A failure here is safe: the superset stays.</summary>
    private async Task WriteExactJournalAsync() => await WriteJournalAsync(_model.HiddenWindows()).ConfigureAwait(false);

    private async Task ActivateLastAsync(Guid desktop)
    {
        // A window that closed meanwhile is skipped for the next most recently used one (S26.4).
        var gone = new HashSet<WindowKey>();
        while (gone.Count < 8 && _model.LastActive(desktop, gone) is { } key)
        {
            var activated = await _foreground.ActivateWindowAsync(key.Handle, CancellationToken.None).ConfigureAwait(false);
            if (activated.Ok || activated.Error!.Code != CoreErrorCode.WindowGone)
            {
                return;
            }

            gone.Add(key);
            _model.Observe(new WindowObservation(key.Handle, ObservationKind.RescanMissing));
        }
    }

    private void Report(IReadOnlyList<WindowKey> stuck)
    {
        foreach (var key in stuck)
        {
            bool first;
            lock (_gate)
            {
                first = _reported.Add(key);
            }

            if (first)
            {
                _diagnostic?.Invoke(new CoreError(CoreErrorCode.WindowNotMovable, "A window refuses to be hidden and stays visible on every desktop.", false));
            }
        }
    }

    private void ClearBusy() => _model.SetDesktops(v => v with { Busy = false, Switching = null });

    private void Save()
    {
        var view = _model.Desktops;
        _ = _store.SaveAsync(view.Desktops, view.Current, CancellationToken.None);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private bool Exists(Guid id) => _model.Desktops.Desktops.Any(d => d.Id == id);

    private static Outcome NotFound(Guid id) => Outcome.Fail(CoreErrorCode.DesktopNotFound, "No desktop has the id " + id.ToString("D") + ".");

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
