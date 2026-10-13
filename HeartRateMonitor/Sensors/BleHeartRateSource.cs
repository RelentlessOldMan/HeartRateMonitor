using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace HeartRateMonitor.Sensors;

/// <summary>
/// Connects to a standard Bluetooth LE heart rate strap and keeps reconnecting until disposed.
/// With no remembered address it connects to the first strap advertising the Heart Rate service.
/// </summary>
public sealed class BleHeartRateSource(ulong? address) : IHeartRateSource
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();
    private ulong? _address = address;

    // Strong references: WinRT drops ValueChanged subscriptions if these are collected.
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly List<GattDeviceService> _services = [];
    private GattCharacteristic? _heartRateCharacteristic;
    private GattCharacteristic? _batteryCharacteristic;
    private TaskCompletionSource? _disconnected;

    public event Action<Models.HeartRateSample>? SampleReceived;
    public event Action<SourceState, string>? StateChanged;
    public event Action<int>? BatteryLevelChanged;
    /// <summary>Raised after each successful connection with the strap's address and name.</summary>
    public event Action<ulong, string>? DeviceConnected;

    public void Start() => _ = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        bool everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Always wait for an advertisement first, even for the remembered strap: Windows can
                // only connect to an unpaired BLE device by address if it has seen it advertise recently.
                StateChanged?.Invoke(SourceState.Searching, _address is null
                    ? "Looking for a heart rate strap…"
                    : "Waiting for the strap (wear it and wet the pads)…");
                _address = await FindStrapAsync(_address, ct);

                StateChanged?.Invoke(everConnected ? SourceState.Reconnecting : SourceState.Connecting, "Connecting…");
                await ConnectAsync(_address.Value);
                everConnected = true;
                StateChanged?.Invoke(SourceState.Connected, _device!.Name);
                await _disconnected!.Task.WaitAsync(ct);
                StateChanged?.Invoke(SourceState.Reconnecting, "Connection lost, reconnecting…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StateChanged?.Invoke(everConnected ? SourceState.Reconnecting : SourceState.Connecting, ex.Message);
            }
            finally
            {
                Cleanup();
            }

            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }

        StateChanged?.Invoke(SourceState.Idle, "Disconnected");
    }

    /// <summary>Waits for <paramref name="wanted"/> to advertise, or for any heart rate strap if null.</summary>
    private static async Task<ulong> FindStrapAsync(ulong? wanted, CancellationToken ct)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        // No advertisement filter: the scan response that carries the name doesn't repeat the
        // service UUID, so filtering would hide it.
        watcher.Received += (_, e) =>
        {
            bool match = wanted is { } address
                ? e.BluetoothAddress == address
                : e.Advertisement.ServiceUuids.Contains(GattServiceUuids.HeartRate);
            if (match)
                found.TrySetResult(e.BluetoothAddress);
        };
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
                found.TrySetException(new InvalidOperationException($"Bluetooth scan failed ({e.Error}). Is Bluetooth on?"));
        };

        using var registration = ct.Register(() => found.TrySetCanceled(ct));
        watcher.Start();
        try
        {
            return await found.Task;
        }
        finally
        {
            watcher.Stop();
        }
    }

    private async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Strap not found. Is it on and being worn?");

        _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var service = await GetServiceAsync(GattServiceUuids.HeartRate)
            ?? throw new InvalidOperationException("Strap has no heart rate service (out of range?)");

        var characteristics = await service.GetCharacteristicsForUuidAsync(
            GattCharacteristicUuids.HeartRateMeasurement, BluetoothCacheMode.Uncached);
        if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
            throw new InvalidOperationException($"Couldn't read heart rate characteristic ({characteristics.Status})");

        _heartRateCharacteristic = characteristics.Characteristics[0];
        _heartRateCharacteristic.ValueChanged += OnHeartRateValueChanged;
        var status = await _heartRateCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Couldn't subscribe to heart rate ({status})");

        await SubscribeBatteryAsync();
        DeviceConnected?.Invoke(address, _device.Name);
    }

    private async Task<GattDeviceService?> GetServiceAsync(Guid uuid)
    {
        var result = await _device!.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
            return null;
        _services.AddRange(result.Services);
        return result.Services[0];
    }

    /// <summary>Battery level is a nice-to-have, so failures here are ignored.</summary>
    private async Task SubscribeBatteryAsync()
    {
        try
        {
            var service = await GetServiceAsync(GattServiceUuids.Battery);
            if (service is null)
                return;

            var characteristics = await service.GetCharacteristicsForUuidAsync(
                GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                return;

            _batteryCharacteristic = characteristics.Characteristics[0];
            var read = await _batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status == GattCommunicationStatus.Success)
                RaiseBattery(read.Value);

            if (_batteryCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                _batteryCharacteristic.ValueChanged += OnBatteryValueChanged;
                await _batteryCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnHeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var sample = HeartRateMeasurementParser.Parse(ToBytes(args.CharacteristicValue), DateTime.Now);
        if (sample is not null)
            SampleReceived?.Invoke(sample);
    }

    private void OnBatteryValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        RaiseBattery(args.CharacteristicValue);

    private void RaiseBattery(IBuffer buffer)
    {
        var bytes = ToBytes(buffer);
        if (bytes.Length > 0)
            BatteryLevelChanged?.Invoke(bytes[0]);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            _disconnected?.TrySetResult();
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void Cleanup()
    {
        if (_heartRateCharacteristic is not null)
            _heartRateCharacteristic.ValueChanged -= OnHeartRateValueChanged;
        if (_batteryCharacteristic is not null)
            _batteryCharacteristic.ValueChanged -= OnBatteryValueChanged;
        _heartRateCharacteristic = null;
        _batteryCharacteristic = null;

        foreach (var service in _services)
            service.Dispose();
        _services.Clear();

        _session?.Dispose();
        _session = null;

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
    }

    // Not disposing _cts: the run loop may still be unwinding and touching its token.
    public void Dispose() => _cts.Cancel();
}
