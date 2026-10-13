namespace HeartRateMonitor.Models;

/// <param name="Time">Local time the measurement arrived.</param>
/// <param name="Bpm">Heart rate in beats per minute.</param>
/// <param name="RrIntervalsMs">Beat-to-beat intervals in milliseconds, if the strap sent any.</param>
/// <param name="SensorContact">Skin contact, or null if the strap doesn't report it.</param>
public sealed record HeartRateSample(DateTime Time, int Bpm, IReadOnlyList<double> RrIntervalsMs, bool? SensorContact);
