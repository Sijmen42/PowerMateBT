using System.Drawing.Drawing2D;

namespace PowerMateBT;

internal sealed class TrayContext : ApplicationContext
{
    const int LowBatteryPercent = 15;

    readonly SynchronizationContext _ui;
    readonly PowerMateDevice _device = new();
    readonly KnobGestures _gestures = new();
    readonly VolumeLight _volumeLight;

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _batteryItem;
    readonly ToolStripMenuItem _autostartItem;
    readonly Icon _connectedIcon = CreateIcon(connected: true);
    readonly Icon _searchingIcon = CreateIcon(connected: false);

    int? _battery;
    bool _lowBatteryWarned;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayContext must be created on the UI thread.");

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _batteryItem = new ToolStripMenuItem { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, OnAutostartClicked)
        {
            Checked = Autostart.IsEnabled,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_batteryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        ShowState(_device.State);
        ShowBattery();

        _volumeLight = new VolumeLight(_device);
        _gestures.VolumeChanged += _volumeLight.VolumeChanged;
        _device.KnobEvent += _gestures.Handle;
        _device.StateChanged += state => _ui.Post(_ => ShowState(state), null);
        _device.BatteryChanged += percent => _ui.Post(_ => OnBattery(percent), null);
        _ = _device.RunAsync();
    }

    void ShowState(PowerMateState state)
    {
        _tray.Icon = state == PowerMateState.Connected ? _connectedIcon : _searchingIcon;
        _statusItem.Text = state switch
        {
            PowerMateState.Connected => "PowerMate connected",
            PowerMateState.NotPaired => "PowerMate not paired - pair it in Bluetooth settings",
            _ => "Searching for PowerMate - turn the knob to wake it",
        };
        UpdateTooltip();
    }

    void OnBattery(int percent)
    {
        _battery = percent;
        ShowBattery();

        if (percent <= LowBatteryPercent && !_lowBatteryWarned)
        {
            _tray.ShowBalloonTip(5000, "PowerMate battery low", $"Battery at {percent}%. Time for new batteries.", ToolTipIcon.Warning);
            _lowBatteryWarned = true;
        }
        else if (percent > LowBatteryPercent + 5)
        {
            _lowBatteryWarned = false;
        }
    }

    void ShowBattery()
    {
        _batteryItem.Text = _battery is int p ? $"Battery: {p}%" : "Battery: -";
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        var state = _device.State == PowerMateState.Connected ? "connected" : "not connected";
        var battery = _battery is int p ? $", battery {p}%" : "";
        _tray.Text = $"PowerMate {state}{battery}";
    }

    void OnAutostartClicked(object? sender, EventArgs e)
    {
        Autostart.Set(!_autostartItem.Checked);
        _autostartItem.Checked = Autostart.IsEnabled;
    }

    void Exit()
    {
        _volumeLight.Dispose();
        _device.Dispose();
        _gestures.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // A small knob: dark with a red LED dot when connected, a grey outline while searching.
    static Icon CreateIcon(bool connected)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var knob = new RectangleF(1, 1, size.Width - 2.5f, size.Height - 2.5f);

            if (connected)
            {
                using var body = new SolidBrush(Color.FromArgb(60, 60, 64));
                g.FillEllipse(body, knob);
                float dot = size.Width / 3f;
                using var led = new SolidBrush(Color.FromArgb(230, 40, 40));
                g.FillEllipse(led, (size.Width - dot) / 2f, (size.Height - dot) / 2f, dot, dot);
            }
            else
            {
                using var outline = new Pen(Color.Gray, 1.5f);
                g.DrawEllipse(outline, knob);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
