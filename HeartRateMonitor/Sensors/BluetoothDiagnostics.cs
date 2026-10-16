using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace HeartRateMonitor.Sensors;

/// <summary>Logs the Bluetooth adapter and Windows-paired LE devices, since both explain a lot of strap trouble.</summary>
public static class BluetoothDiagnostics
{
    public static async Task LogAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Log.Warn("Bluetooth: no adapter found");
                return;
            }

            var info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId);
            var radio = await adapter.GetRadioAsync();
            Log.Info($"Bluetooth: adapter \"{info.Name}\" {BleHeartRateSource.FormatAddress(adapter.BluetoothAddress)}, " +
                     $"radio {radio?.State.ToString() ?? "unknown"}, LE {adapter.IsLowEnergySupported}, " +
                     $"central role {adapter.IsCentralRoleSupported}, max advertisement {adapter.MaxAdvertisementDataLength} bytes");
            if (radio is { State: not RadioState.On })
                Log.Warn("Bluetooth: the radio is not on");

            var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            Log.Info(paired.Count == 0
                ? "Bluetooth: no LE devices paired in Windows"
                : $"Bluetooth: LE devices paired in Windows: {string.Join(", ", paired.Select(d => $"\"{d.Name}\""))}");
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth: diagnostics failed", ex);
        }
    }
}
