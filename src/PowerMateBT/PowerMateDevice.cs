using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;

namespace PowerMateBT;

internal enum PowerMateState
{
    NotPaired,
    Searching,
    Connected,
}

/// <summary>
/// Connection to a paired Griffin PowerMate Bluetooth. The knob is not a HID device; it exposes a
/// vendor GATT service with a one-byte "knob event" notification and a battery-percentage
/// notification. The knob sleeps when idle, so this class keeps retrying until it wakes and
/// re-subscribes whenever the connection comes back.
/// </summary>
internal sealed class PowerMateDevice : IDisposable
{
    static readonly Guid ServiceUuid = new("25598cf7-4240-40a6-9910-080f19f91ebc");
    static readonly Guid KnobUuid = new("9cf53570-ddd9-47f3-ba63-09acefc60415");
    static readonly Guid BatteryUuid = new("50f09cc9-fe1d-4c79-a962-b3a7cd3e5584");
    static readonly Guid LedUuid = new("847d189e-86ee-4bd2-966f-800832b1259d");

    const int FindRetryMs = 5000;
    const int ConnectRetryMs = 2000;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _setupLock = new(1, 1);

    BluetoothLEDevice? _device;
    GattSession? _session;
    GattDeviceService? _service;
    GattCharacteristic? _knob;
    GattCharacteristic? _battery;
    GattCharacteristic? _led;

    public event Action<byte>? KnobEvent;
    public event Action<int>? BatteryChanged;
    public event Action<PowerMateState>? StateChanged;

    public PowerMateState State { get; private set; } = PowerMateState.Searching;

    public async Task RunAsync()
    {
        try
        {
            while ((_device = await FindAsync()) is null)
            {
                SetState(PowerMateState.NotPaired);
                await Task.Delay(FindRetryMs, _cts.Token);
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Ask Windows to keep (re)connecting to the knob for as long as we're running.
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            _session.MaintainConnection = true;

            await ConnectAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    static async Task<BluetoothLEDevice?> FindAsync()
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector);
        var info = infos.FirstOrDefault(i => i.Name.Contains("PowerMate", StringComparison.OrdinalIgnoreCase));
        return info is null ? null : await BluetoothLEDevice.FromIdAsync(info.Id);
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            SetState(PowerMateState.Searching);

        _ = ConnectAsync();
    }

    async Task ConnectAsync()
    {
        try
        {
            await _setupLock.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (State == PowerMateState.Connected && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    return;

                SetState(PowerMateState.Searching);
                if (await TrySetupAsync())
                {
                    SetState(PowerMateState.Connected);
                    return;
                }

                // Usually "Unreachable": the knob is asleep until someone touches it.
                await Task.Delay(ConnectRetryMs, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _setupLock.Release();
        }
    }

    async Task<bool> TrySetupAsync()
    {
        ReleaseService();
        if (_device is null)
            return false;

        try
        {
            var services = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                return false;

            _service = services.Services[0];
            var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success)
                return false;

            _knob = chars.Characteristics.FirstOrDefault(c => c.Uuid == KnobUuid);
            _battery = chars.Characteristics.FirstOrDefault(c => c.Uuid == BatteryUuid);
            _led = chars.Characteristics.FirstOrDefault(c => c.Uuid == LedUuid);
            if (_knob is null)
                return false;

            _knob.ValueChanged += OnKnobValueChanged;
            var status = await _knob.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
                return false;

            if (_battery is not null)
            {
                _battery.ValueChanged += OnBatteryValueChanged;
                await _battery.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
            }

            return true;
        }
        catch (Exception)
        {
            // The knob can drop off mid-setup (ObjectDisposedException, COMException...); just retry.
            return false;
        }
    }

    /// <summary>
    /// Writes one byte to the LED. Measured values: 00 off, BF steady bright, 01..7E on for a
    /// moment (longer for higher values), A0 double pulse then on, A1/C0 keep pulsing.
    /// </summary>
    public async Task SetLedAsync(byte value)
    {
        var led = _led;
        if (led is null)
            return;

        try
        {
            var result = await led.WriteValueWithResultAsync(
                CryptographicBuffer.CreateFromByteArray(new[] { value }), GattWriteOption.WriteWithResponse);
            Log.Write($"led {value:X2}: {result.Status}");
        }
        catch (Exception ex)
        {
            // The knob may have just gone to sleep; the LED state isn't worth failing over.
            Log.Write($"led {value:X2}: {ex.GetType().Name}");
        }
    }

    void OnKnobValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] bytes);
        if (bytes.Length > 0)
            KnobEvent?.Invoke(bytes[0]);
    }

    void OnBatteryValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] bytes);
        if (bytes.Length > 0)
            BatteryChanged?.Invoke(bytes[0]);
    }

    void SetState(PowerMateState state)
    {
        if (State == state)
            return;

        State = state;
        Log.Write($"state {state}");
        StateChanged?.Invoke(state);
    }

    void ReleaseService()
    {
        if (_knob is not null)
            _knob.ValueChanged -= OnKnobValueChanged;
        if (_battery is not null)
            _battery.ValueChanged -= OnBatteryValueChanged;

        _service?.Dispose();
        _service = null;
        _knob = null;
        _battery = null;
        _led = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        ReleaseService();
        if (_device is not null)
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _session?.Dispose();
        _device?.Dispose();
    }
}
