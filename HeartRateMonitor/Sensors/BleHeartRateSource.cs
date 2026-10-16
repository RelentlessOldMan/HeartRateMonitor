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
    /// <summary>How long a dropped link may take to come back on its own before we start over.</summary>
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(6);
    /// <summary>Straps send about once a second; this long without data means the link is broken.</summary>
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SampleLogInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private ulong? _address = address;

    // Strong references: WinRT drops ValueChanged subscriptions if these are collected.
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly List<GattDeviceService> _services = [];
    private GattCharacteristic? _heartRateCharacteristic;
    private GattCharacteristic? _batteryCharacteristic;

    // Written from WinRT callback threads, read by the monitor loop (UTC ticks, 0 = none).
    private long _lastSampleTicks;
    private long _disconnectedSinceTicks;
    private int _samplesSinceLog;
    private volatile Models.HeartRateSample? _lastSample;
    private int _lastBatteryLevel = -1;

    public event Action<Models.HeartRateSample>? SampleReceived;
    public event Action<SourceState, string>? StateChanged;
    public event Action<int>? BatteryLevelChanged;
    /// <summary>Raised after each successful connection with the strap's address and name.</summary>
    public event Action<ulong, string>? DeviceConnected;

    public void Start() => _ = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"BLE: starting, remembered strap {(_address is { } a ? FormatAddress(a) : "none")}");
        bool everConnected = false;
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                // Always wait for an advertisement first, even for the remembered strap: Windows can
                // only connect to an unpaired BLE device by address if it has seen it advertise recently.
                SetState(SourceState.Searching, _address is null
                    ? "Looking for a heart rate strap…"
                    : "Waiting for the strap (wear it and wet the pads)…");
                var (found, addressType) = await FindStrapAsync(_address, ct);
                _address = found;

                SetState(everConnected ? SourceState.Reconnecting : SourceState.Connecting, "Connecting…");
                await ConnectAsync(found, addressType);
                everConnected = true;
                attempt = 0;
                SetState(SourceState.Connected, _device!.Name);
                await MonitorAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: attempt {attempt} failed: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
                SetState(everConnected ? SourceState.Reconnecting : SourceState.Connecting, ex.Message);
            }
            finally
            {
                Cleanup();
            }

            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }

        Log.Info("BLE: stopped");
        StateChanged?.Invoke(SourceState.Idle, "Disconnected");
    }

    private void SetState(SourceState state, string text)
    {
        Log.Info($"BLE: state {state}: {text}");
        StateChanged?.Invoke(state, text);
    }

    /// <summary>
    /// Waits for <paramref name="wanted"/> to advertise, or for any heart rate strap if null.
    /// Returns the address type too: many straps use random addresses, and connecting to one
    /// as if it were a public address can give a link that connects but never delivers data.
    /// </summary>
    private static async Task<(ulong Address, BluetoothAddressType Type)> FindStrapAsync(ulong? wanted, CancellationToken ct)
    {
        var found = new TaskCompletionSource<(ulong, BluetoothAddressType)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        // No advertisement filter: the scan response that carries the name doesn't repeat the
        // service UUID, so filtering would hide it.
        watcher.Received += (_, e) =>
        {
            bool match = wanted is { } address
                ? e.BluetoothAddress == address
                : e.Advertisement.ServiceUuids.Contains(GattServiceUuids.HeartRate);
            if (match && found.TrySetResult((e.BluetoothAddress, e.BluetoothAddressType)))
            {
                Log.Info($"BLE: found {FormatAddress(e.BluetoothAddress)} ({e.BluetoothAddressType}) " +
                         $"name \"{e.Advertisement.LocalName}\" RSSI {e.RawSignalStrengthInDBm} dBm, {e.AdvertisementType}, " +
                         $"services [{string.Join(", ", e.Advertisement.ServiceUuids.Select(ShortUuid))}]");
            }
        };
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
                found.TrySetException(new InvalidOperationException($"Bluetooth scan failed ({e.Error}). Is Bluetooth on?"));
        };

        using var registration = ct.Register(() => found.TrySetCanceled(ct));
        watcher.Start();
        Log.Info("BLE: scanning");
        try
        {
            return await found.Task;
        }
        finally
        {
            watcher.Stop();
        }
    }

    private async Task ConnectAsync(ulong address, BluetoothAddressType addressType)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType)
            ?? throw new InvalidOperationException("Strap not found. Is it on and being worn?");
        Log.Info($"BLE: device \"{_device.Name}\" id {_device.DeviceId}, paired in Windows: {_device.DeviceInformation.Pairing.IsPaired}, " +
                 $"status {_device.ConnectionStatus}");

        Interlocked.Exchange(ref _lastSampleTicks, 0);
        Interlocked.Exchange(ref _disconnectedSinceTicks, 0);
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
        await SubscribeHeartRateAsync();

        await SubscribeBatteryAsync();
        Log.Info($"BLE: connected, MTU {_session.MaxPduSize}, status {_device.ConnectionStatus}");
        DeviceConnected?.Invoke(address, _device.Name);
    }

    private async Task SubscribeHeartRateAsync()
    {
        var status = await _heartRateCharacteristic!.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        Log.Info($"BLE: heart rate subscribe: {status}");
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Couldn't subscribe to heart rate ({status})");
    }

    /// <summary>
    /// Watches the connection until it's beyond saving. A brief drop is given time to recover
    /// (Windows reconnects by itself because of MaintainConnection), after which we subscribe
    /// again since an unpaired strap forgets subscriptions between connections. A link that
    /// stays up but stops delivering heart rate is torn down too.
    /// </summary>
    private async Task MonitorAsync(CancellationToken ct)
    {
        var connectedAt = DateTime.UtcNow;
        var lastSampleLog = DateTime.UtcNow;
        bool dropped = false;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var now = DateTime.UtcNow;

            long disconnectedSince = Interlocked.Read(ref _disconnectedSinceTicks);
            if (disconnectedSince != 0)
            {
                if (!dropped)
                {
                    dropped = true;
                    SetState(SourceState.Reconnecting, "Signal dropped, waiting for it to come back…");
                }
                if (now - new DateTime(disconnectedSince, DateTimeKind.Utc) > DisconnectGrace)
                    throw new InvalidOperationException("Connection lost, reconnecting…");
                continue;
            }

            if (dropped)
            {
                dropped = false;
                Log.Info("BLE: link came back, subscribing again");
                await SubscribeHeartRateAsync();
                connectedAt = now;
                SetState(SourceState.Connected, _device!.Name);
            }

            long lastSample = Interlocked.Read(ref _lastSampleTicks);
            var lastData = lastSample != 0 && lastSample > connectedAt.Ticks
                ? new DateTime(lastSample, DateTimeKind.Utc)
                : connectedAt;
            if (now - lastData > NoDataTimeout)
                throw new InvalidOperationException($"Connected but no heart rate for {NoDataTimeout.TotalSeconds:0} s, reconnecting…");

            if (now - lastSampleLog >= SampleLogInterval)
            {
                Log.Info($"BLE: {Interlocked.Exchange(ref _samplesSinceLog, 0)} samples in the last {(now - lastSampleLog).TotalSeconds:0} s, latest {_lastSample?.Bpm} bpm ({BeatsText(_lastSample)}), contact {_lastSample?.SensorContact?.ToString() ?? "n/a"}");
                lastSampleLog = now;
            }
        }
    }

    private async Task<GattDeviceService?> GetServiceAsync(Guid uuid)
    {
        var result = await _device!.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        Log.Info($"BLE: service {ShortUuid(uuid)}: {result.Status}, {result.Services.Count} found");
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
        catch (Exception ex)
        {
            Log.Warn($"BLE: battery unavailable: {ex.Message}");
        }
    }

    private void OnHeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = ToBytes(args.CharacteristicValue);
        var sample = HeartRateMeasurementParser.Parse(bytes, DateTime.Now);
        // Log the very first packet of each connection raw, in case a strap encodes things oddly.
        if (Interlocked.Exchange(ref _lastSampleTicks, DateTime.UtcNow.Ticks) == 0)
            Log.Info($"BLE: first heart rate packet [{Convert.ToHexString(bytes)}] -> {sample?.Bpm.ToString() ?? "unparseable"} bpm ({BeatsText(sample)}), contact {sample?.SensorContact?.ToString() ?? "n/a"}");
        Interlocked.Increment(ref _samplesSinceLog);
        if (sample is not null)
        {
            _lastSample = sample;
            SampleReceived?.Invoke(sample);
        }
    }

    private void OnBatteryValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        RaiseBattery(args.CharacteristicValue);

    private void RaiseBattery(IBuffer buffer)
    {
        var bytes = ToBytes(buffer);
        // Some straps send the battery level every second; only pass on (and log) actual changes.
        if (bytes.Length > 0 && Interlocked.Exchange(ref _lastBatteryLevel, bytes[0]) != bytes[0])
        {
            Log.Info($"BLE: battery {bytes[0]}%");
            BatteryLevelChanged?.Invoke(bytes[0]);
        }
    }

    /// <summary>What the packet's RR intervals alone say the heart rate is, to compare against the strap's number.</summary>
    private static string BeatsText(Models.HeartRateSample? sample) =>
        sample is { RrIntervalsMs.Count: > 0 } s
            ? $"beats say {60000 / s.RrIntervalsMs.Average():0}"
            : "no beat intervals";

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        var status = sender.ConnectionStatus;
        Log.Info($"BLE: connection status {status}");
        if (status == BluetoothConnectionStatus.Disconnected)
            Interlocked.CompareExchange(ref _disconnectedSinceTicks, DateTime.UtcNow.Ticks, 0);
        else
            Interlocked.Exchange(ref _disconnectedSinceTicks, 0);
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (40 - 8 * i)) & 0xFF).ToString("X2")));

    /// <summary>"180D" for standard Bluetooth UUIDs, the full GUID otherwise.</summary>
    private static string ShortUuid(Guid uuid)
    {
        var text = uuid.ToString();
        return text.EndsWith("-0000-1000-8000-00805f9b34fb") && text.StartsWith("0000")
            ? text.Substring(4, 4).ToUpperInvariant()
            : text;
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
