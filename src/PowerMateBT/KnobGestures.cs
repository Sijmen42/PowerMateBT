namespace PowerMateBT;

/// <summary>
/// Turns raw PowerMate event bytes into actions.
///
///   0x67 / 0x68  turn anticlockwise / clockwise
///   0x69 / 0x70  turn anticlockwise / clockwise while pressed
///   0x72..0x77   still held (ticks)
///   0x65         released (after a click or a press-and-turn)
///   0x66         released after a long hold
///
/// The release byte is unreliable: it is sometimes not sent after a press-and-turn and then turns
/// up later, duplicated alongside the next release. So track skipping does not wait for the
/// release; it fires once a press-and-turn burst goes quiet. A release is only taken as a click
/// when it is not a duplicate and no press-and-turn happened just before it.
/// </summary>
internal sealed class KnobGestures : IDisposable
{
    static readonly TimeSpan BurstEnd = TimeSpan.FromMilliseconds(400);
    const long DuplicateReleaseMs = 300;
    const long TurnBeforeReleaseMs = 1500;

    readonly object _lock = new();
    readonly System.Threading.Timer _burstTimer;

    int _burstSteps; // net clockwise steps in the current press-and-turn burst
    long _lastPressedTurnMs = long.MinValue / 2;
    long _lastReleaseMs = long.MinValue / 2;

    public KnobGestures() => _burstTimer = new System.Threading.Timer(_ => EndBurst());

    public event Action? VolumeChanged;

    public void Handle(byte code)
    {
        Log.Write($"knob {code:X2}");
        lock (_lock)
        {
            switch (code)
            {
                case 0x67:
                    MediaKeys.VolumeDown();
                    VolumeChanged?.Invoke();
                    break;
                case 0x68:
                    MediaKeys.VolumeUp();
                    VolumeChanged?.Invoke();
                    break;
                case 0x69:
                    PressedTurn(-1);
                    break;
                case 0x70:
                    PressedTurn(+1);
                    break;
                case 0x65:
                    Release();
                    break;
                // 0x66 (release after a long hold) and 0x72..0x77 (hold ticks) do nothing.
            }
        }
    }

    void PressedTurn(int direction)
    {
        _burstSteps += direction;
        _lastPressedTurnMs = Environment.TickCount64;
        _burstTimer.Change(BurstEnd, Timeout.InfiniteTimeSpan);
    }

    void EndBurst()
    {
        lock (_lock)
        {
            int steps = _burstSteps;
            _burstSteps = 0;

            // Use the overall direction, so the stray tick you often get when pushing down is ignored.
            Log.Write($"  -> press-and-turn, net {steps:+0;-0;0} steps");
            if (steps > 0)
                MediaKeys.NextTrack();
            else if (steps < 0)
                MediaKeys.PreviousTrack();
        }
    }

    void Release()
    {
        long now = Environment.TickCount64;
        bool duplicate = now - _lastReleaseMs < DuplicateReleaseMs;
        _lastReleaseMs = now;

        if (duplicate)
        {
            Log.Write("  (duplicate release ignored)");
            return;
        }

        if (now - _lastPressedTurnMs < TurnBeforeReleaseMs)
        {
            Log.Write("  (release ends a press-and-turn)");
            return;
        }

        Log.Write("  -> play/pause");
        MediaKeys.PlayPause();
    }

    public void Dispose() => _burstTimer.Dispose();
}
