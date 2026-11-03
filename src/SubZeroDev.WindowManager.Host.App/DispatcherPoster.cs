using Microsoft.UI.Dispatching;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>Runs work on the shell's UI thread through its dispatcher queue, in order.</summary>
internal sealed class DispatcherPoster(DispatcherQueue queue) : IUiPoster
{
    public void Post(Action action) => queue.TryEnqueue(() => action());
}
