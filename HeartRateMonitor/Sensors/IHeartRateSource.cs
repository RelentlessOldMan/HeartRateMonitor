using HeartRateMonitor.Models;

namespace HeartRateMonitor.Sensors;

public enum SourceState { Idle, Searching, Connecting, Connected, Reconnecting }

/// <summary>Something that produces heart rate samples: the real BLE strap or the dummy generator.</summary>
/// <remarks>Events may be raised on any thread.</remarks>
public interface IHeartRateSource : IDisposable
{
    event Action<HeartRateSample>? SampleReceived;
    event Action<SourceState, string>? StateChanged;
    event Action<int>? BatteryLevelChanged;

    void Start();
}
