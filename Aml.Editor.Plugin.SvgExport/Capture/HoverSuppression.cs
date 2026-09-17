using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Aml.Editor.Plugin.SvgExport.Capture;

/// <summary>
/// Removes the mouse-over state from a window for the duration of a capture, so the row or
/// button under the pointer is not exported highlighted. Making the window hit-test
/// invisible and re-synchronizing the mouse clears IsMouseOver everywhere; restoring it
/// brings the state back.
/// </summary>
public sealed class HoverSuppression : IDisposable
{
    private readonly Window _window;
    private readonly bool _previous;

    private HoverSuppression(Window window)
    {
        _window = window;
        _previous = window.IsHitTestVisible;
    }

    public static async Task<HoverSuppression> BeginAsync(Window window)
    {
        var s = new HoverSuppression(window);
        window.IsHitTestVisible = false;
        Mouse.Synchronize();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        return s;
    }

    public void Dispose()
    {
        _window.IsHitTestVisible = _previous;
        Mouse.Synchronize();
    }
}
