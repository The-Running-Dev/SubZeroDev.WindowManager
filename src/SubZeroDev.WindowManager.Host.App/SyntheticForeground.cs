using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// The test hook's foreground service (S31, I38): activating a model-only synthetic window makes it the foreground window in
/// the model's eyes, and nothing else; every other handle goes to the real service, as before. A synthetic handle is never
/// a real window, so the real service would only report it gone.
/// </summary>
internal sealed class SyntheticForeground(IForegroundService inner) : IForegroundService
{
    private SyntheticWindows? _windows;

    /// <summary>The synthetic windows; the model needs the foreground service first, so they are bound afterwards.</summary>
    /// <param name="windows">The model-only windows.</param>
    public void Bind(SyntheticWindows windows) => _windows = windows;

    public void AllowForeground(int processId) => inner.AllowForeground(processId);

    public Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct) =>
        _windows?.Activate(hwnd) == true ? Task.FromResult(Outcome.Success) : inner.ActivateWindowAsync(hwnd, ct);

    public Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct) => inner.MinimizeWindowAsync(hwnd, ct);

    public Task<Outcome<ActivationResult>> ActivateConfirmedAsync(nint hwnd, CancellationToken ct) =>
        _windows?.Activate(hwnd) == true
            ? Task.FromResult(Outcome<ActivationResult>.Success(ActivationResult.Foreground))
            : inner.ActivateConfirmedAsync(hwnd, ct);
}
