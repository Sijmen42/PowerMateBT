namespace PowerMateBT;

/// <summary>
/// Keeps the knob's LED off, except while the volume is being changed: it comes on with the first
/// volume step and goes off again shortly after the last one.
/// </summary>
internal sealed class VolumeLight : IDisposable
{
    const byte LedOn = 0xBF;
    const byte LedOff = 0x00;
    static readonly TimeSpan OnTime = TimeSpan.FromSeconds(1);

    readonly PowerMateDevice _device;
    readonly System.Threading.Timer _offTimer;
    readonly object _lock = new();
    bool _isOn;

    public VolumeLight(PowerMateDevice device)
    {
        _device = device;
        _offTimer = new System.Threading.Timer(_ => TurnOff());
        _device.StateChanged += OnStateChanged;
    }

    public void VolumeChanged()
    {
        lock (_lock)
        {
            if (!_isOn)
            {
                _isOn = true;
                _ = _device.SetLedAsync(LedOn);
            }

            _offTimer.Change(OnTime, Timeout.InfiniteTimeSpan);
        }
    }

    void TurnOff()
    {
        lock (_lock)
        {
            _isOn = false;
            _ = _device.SetLedAsync(LedOff);
        }
    }

    // The knob shows steady red by itself once connected, so switch it off straight away.
    void OnStateChanged(PowerMateState state)
    {
        if (state == PowerMateState.Connected)
            TurnOff();
    }

    public void Dispose()
    {
        _device.StateChanged -= OnStateChanged;
        _offTimer.Dispose();
    }
}
