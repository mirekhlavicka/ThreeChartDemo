using System.Diagnostics;

namespace ThreeChartDemo.Services;

/// <summary>
/// The browser has one thread. Long loops call <see cref="YieldIfNeededAsync"/> now and then:
/// when ~40 ms have passed it gives the UI a chance to repaint (progress bar, buttons).
/// </summary>
public sealed class TimeSlicer
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public async ValueTask YieldIfNeededAsync(CancellationToken ct = default)
    {
        if (_sw.ElapsedMilliseconds < 40) return;
        ct.ThrowIfCancellationRequested();
        await Task.Delay(1, ct);
        _sw.Restart();
    }
}
