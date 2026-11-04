using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.Surfaces;

// The decisions behind the Thumbnail element and the hoverPopup prop, kept free of WinUI so the tests
// can run them without a display. The tests compile this file in directly.

/// <summary>Why a Thumbnail shows its card instead of the live window; each is logged once per element.</summary>
internal enum ThumbnailProblem { ThumbnailUnavailable, StaleWindowReference, WindowGone }

/// <summary>
/// What a Thumbnail should show: the live window (<see cref="Live"/>, to be registered with the thumbnail
/// service) or its icon-and-title card. <see cref="Problem"/> is set when the card shows because something
/// failed, rather than because the window is minimised or hidden.
/// </summary>
internal readonly record struct ThumbnailDecision(bool Live, ThumbnailProblem? Problem, string Detail, nint Handle, ManagedWindow? Window)
{
    public static ThumbnailDecision Card(ThumbnailProblem? problem, string detail, nint handle = 0, ManagedWindow? window = null) =>
        new(false, problem, detail, handle, window);
}

internal static class ThumbnailPolicy
{
    /// <summary>The hoverPopup delay when the prop gives none (contract: <c>delayMs = 400</c>).</summary>
    public const double DefaultHoverDelayMs = 400;
    /// <summary>The token suffix for a <c>size</c> prop: <c>Small</c>, <c>Large</c>, else <c>Medium</c>.</summary>
    public static string SizeSuffix(string? size) => size switch
    {
        "small" => "Small",
        "large" => "Large",
        _ => "Medium",
    };

    /// <summary>
    /// The element's size in DIPs, from <c>Wm.Thumbnail.Width.*</c> and <c>Wm.Thumbnail.Height.*</c>; a token
    /// that is missing, not a double, or not positive falls back to the host's value.
    /// </summary>
    public static (double Width, double Height) SizeOf(string? size, Func<string, object?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var suffix = SizeSuffix(size);
        return (Token("Wm.Thumbnail.Width." + suffix), Token("Wm.Thumbnail.Height." + suffix));

        double Token(string key) =>
            lookup(key) is double themed && double.IsFinite(themed) && themed > 0 ? themed : ThemePackageReader.HostSizeTokens[key];
    }

    /// <summary>Whether DWM has something to draw: shown, not minimised, not hidden by the shell and not cloaked.</summary>
    public static bool Drawable(ManagedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Visible && !window.Minimised && !window.ShellHidden && window.Cloak == CloakState.None;
    }

    /// <summary>
    /// Decides between the live window and the card. No key shows an empty card without a problem; a key
    /// that does not resolve, or a host without a window model or thumbnail service, shows the card with
    /// the problem to log; a window that cannot be drawn now shows its card without one.
    /// </summary>
    public static ThumbnailDecision Decide(string? windowKey, Func<string, Outcome<WindowTarget>>? resolve, bool haveService)
    {
        if (string.IsNullOrEmpty(windowKey))
        {
            return ThumbnailDecision.Card(null, "No window is named.");
        }

        if (resolve is null)
        {
            return ThumbnailDecision.Card(ThumbnailProblem.ThumbnailUnavailable, "The host has no window model.");
        }

        var resolved = resolve(windowKey);
        if (!resolved.Ok)
        {
            var problem = resolved.Error!.Code switch
            {
                CoreErrorCode.StaleWindowReference => ThumbnailProblem.StaleWindowReference,
                CoreErrorCode.WindowGone => ThumbnailProblem.WindowGone,
                _ => ThumbnailProblem.ThumbnailUnavailable,
            };
            return ThumbnailDecision.Card(problem, resolved.Error.Detail);
        }

        var target = resolved.Value!;
        if (target.Managed is not { } window)
        {
            return ThumbnailDecision.Card(ThumbnailProblem.ThumbnailUnavailable, "The window is not one the shell tracks.", target.Handle);
        }

        if (!haveService)
        {
            return ThumbnailDecision.Card(ThumbnailProblem.ThumbnailUnavailable, "The host has no thumbnail service.", target.Handle, window);
        }

        return Drawable(window)
            ? new ThumbnailDecision(true, null, string.Empty, target.Handle, window)
            : ThumbnailDecision.Card(null, "The window is not on screen.", target.Handle, window);
    }

    /// <summary>The <c>windowKey</c> of an icon ref <c>{"window": "wk1:…"}</c>, or null for any other ref (a handle included).</summary>
    public static string? IconWindowKey(JsonNode? icon) =>
        icon is JsonObject { Count: 1 } body && body["window"] is JsonValue value && value.TryGetValue<string>(out var key) ? key : null;

    /// <summary>The <c>hoverPopup</c> prop as a popup id and a delay; null when it names no popup. A missing or negative delay is 400 ms.</summary>
    public static (string PopupId, TimeSpan Delay)? HoverPopupOf(JsonNode? prop)
    {
        if (prop is not JsonObject body || body["popup"] is not JsonValue popup || !popup.TryGetValue<string>(out var popupId) || popupId.Length == 0)
        {
            return null;
        }

        var delay = body["delayMs"] is JsonValue ms && ms.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= 0
            ? number
            : DefaultHoverDelayMs;
        return (popupId, TimeSpan.FromMilliseconds(delay));
    }

    /// <summary>A rectangle in DIPs from the window's client origin, in client pixels at <paramref name="scale"/>.</summary>
    public static RectInt ClientPixels(double x, double y, double width, double height, double scale) =>
        new((int)Math.Round(x * scale), (int)Math.Round(y * scale), (int)Math.Round((x + width) * scale), (int)Math.Round((y + height) * scale));

    /// <summary>
    /// Whether the live thumbnail should be drawn: the element is loaded, visible all the way up, inside
    /// its scroll viewport, has an area, and overlaps the window's client area.
    /// </summary>
    public static bool ShouldDraw(bool loaded, bool visibleInTree, bool viewportEmpty, RectInt bounds, int clientWidth, int clientHeight) =>
        loaded && visibleInTree && !viewportEmpty && bounds.Width > 0 && bounds.Height > 0
        && bounds.Right > 0 && bounds.Bottom > 0 && bounds.Left < clientWidth && bounds.Top < clientHeight;
}

/// <summary>Remembers which problems an element has logged, so each is logged once.</summary>
internal sealed class ThumbnailProblemLog
{
    private readonly HashSet<ThumbnailProblem> _logged = [];

    /// <summary>True the first time <paramref name="problem"/> is seen.</summary>
    public bool First(ThumbnailProblem problem) => _logged.Add(problem);
}

/// <summary>What the hover controller must do after an input: the timer runs one wait at a time.</summary>
internal enum HoverAction { None, StartOpenTimer, StartCloseTimer, StopTimer, Open, Close }

internal enum HoverPhase { Idle, Waiting, Open }

/// <summary>
/// The hoverPopup state machine. The pointer resting on the element for the delay opens the popup;
/// leaving both the element and the popup starts a short grace wait (so moving from one to the other
/// does not close it), after which it closes. A popup closed by any other path returns it to idle.
/// </summary>
internal sealed class HoverPopupState
{
    /// <summary>How long the pointer may be outside both the element and the popup before it closes.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromMilliseconds(250);

    private bool _overElement;
    private bool _overPopup;

    public HoverPhase Phase { get; private set; }

    public HoverAction ElementEntered()
    {
        _overElement = true;
        return Phase switch
        {
            HoverPhase.Idle => Wait(),
            HoverPhase.Open => HoverAction.StopTimer,
            _ => HoverAction.None,
        };

        HoverAction Wait()
        {
            Phase = HoverPhase.Waiting;
            return HoverAction.StartOpenTimer;
        }
    }

    public HoverAction ElementExited()
    {
        _overElement = false;
        switch (Phase)
        {
            case HoverPhase.Waiting:
                Phase = HoverPhase.Idle;
                return HoverAction.StopTimer;
            case HoverPhase.Open when !_overPopup:
                return HoverAction.StartCloseTimer;
            default:
                return HoverAction.None;
        }
    }

    public HoverAction PopupEntered()
    {
        _overPopup = true;
        return Phase == HoverPhase.Open ? HoverAction.StopTimer : HoverAction.None;
    }

    public HoverAction PopupExited()
    {
        _overPopup = false;
        return Phase == HoverPhase.Open && !_overElement ? HoverAction.StartCloseTimer : HoverAction.None;
    }

    public HoverAction TimerElapsed()
    {
        switch (Phase)
        {
            case HoverPhase.Waiting:
                Phase = HoverPhase.Open;
                return HoverAction.Open;
            case HoverPhase.Open when !_overElement && !_overPopup:
                Phase = HoverPhase.Idle;
                return HoverAction.Close;
            default:
                return HoverAction.None;
        }
    }

    /// <summary>The popup closed, failed to open, or the element went away: back to idle.</summary>
    public void Closed()
    {
        Phase = HoverPhase.Idle;
        _overPopup = false;
    }
}
