using HeartRateMonitor.Models;

namespace HeartRateMonitor.Sensors;

/// <summary>
/// Decodes the standard Bluetooth "Heart Rate Measurement" characteristic (0x2A37).
/// Layout: flags byte, then heart rate (uint8 or uint16), optional energy expended (uint16),
/// optional RR intervals (uint16 each, in 1/1024 s).
/// </summary>
public static class HeartRateMeasurementParser
{
    private const byte HeartRateIsUInt16 = 0x01;
    private const byte SensorContactDetected = 0x02;
    private const byte SensorContactSupported = 0x04;
    private const byte EnergyExpendedPresent = 0x08;
    private const byte RrIntervalsPresent = 0x10;

    public static HeartRateSample? Parse(ReadOnlySpan<byte> data, DateTime time)
    {
        if (data.Length < 2)
            return null;

        byte flags = data[0];
        int index = 1;

        int bpm;
        if ((flags & HeartRateIsUInt16) != 0)
        {
            if (data.Length < 3)
                return null;
            bpm = data[1] | (data[2] << 8);
            index += 2;
        }
        else
        {
            bpm = data[1];
            index += 1;
        }

        bool? contact = (flags & SensorContactSupported) != 0
            ? (flags & SensorContactDetected) != 0
            : null;

        if ((flags & EnergyExpendedPresent) != 0)
            index += 2;

        var rr = new List<double>();
        if ((flags & RrIntervalsPresent) != 0)
        {
            for (; index + 1 < data.Length; index += 2)
                rr.Add((data[index] | (data[index + 1] << 8)) * 1000.0 / 1024.0);
        }

        return new HeartRateSample(time, bpm, rr, contact);
    }
}
