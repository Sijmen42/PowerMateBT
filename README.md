# PowerMateBT

Turn your **Griffin PowerMate Bluetooth** into a volume and media knob on Windows.

Griffin only ever released software for the Bluetooth PowerMate on macOS. On Windows the knob
pairs but does nothing. PowerMateBT is a small tray app that fixes that. It's a normal program,
so there's no driver to install and no admin rights needed.

## Overview

| Knob | Action |
|---|---|
| Turn | Volume down / up (with the usual Windows volume pop-up) |
| Click | Play / pause |
| Press and turn | Next track (clockwise) / previous track (anticlockwise) |
| Long hold | Nothing |

- **Light:** the knob's LED stays off and lights up only while you change the volume.
- **Tray icon:** shows whether the knob is connected. Its menu shows the battery level, and it
  warns you when the battery drops to 15%.
- **Automatic reconnect:** the knob sleeps when idle. Touch it and it reconnects by itself.
- **Start with Windows:** an option in the tray menu.
- **Works with** any media player that responds to keyboard media keys, such as Spotify, YouTube
  Music in a browser, or Windows Media Player.

> **Status:** tested on one PowerMate Bluetooth (FCC ID PAV05358, firmware 1.0.1). Reports from
> other units are welcome.

## Getting started

You need Windows 10 (build 19041) or later, with Bluetooth LE.

1. **Pair the knob:** Settings → Bluetooth & devices → Add device → Bluetooth, then select
   **PowerMate Bluetooth**. If Windows asks for a PIN, try `0000`.
2. **Download** `PowerMateBT.exe` from the
   [latest release](https://github.com/Sijmen42/PowerMateBT/releases/latest). It's a single file
   with everything included, so you don't need to install .NET. Put it somewhere permanent, such
   as `%LOCALAPPDATA%\PowerMateBT\app`.
3. **Run it.** Windows SmartScreen may warn you the first time, because the exe isn't
   code-signed. Click **More info → Run anyway**. Turn the knob to wake it, and the tray icon gets a
   red dot when it's connected.
4. Right-click the tray icon and tick **Start with Windows** if you want it to start automatically.

### Building from source

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then:

```
dotnet publish src/PowerMateBT -c Release -o publish
```

This produces the same single `publish\PowerMateBT.exe`. While developing, use
`dotnet run --project src/PowerMateBT`.

## How it works

The Bluetooth PowerMate isn't a standard Bluetooth input (HID) device, which is why Windows
can't use it on its own. Instead it has its own Bluetooth LE service. PowerMateBT connects to that
service, reads the knob events, and sends the matching media keys to Windows. A log of recent
events is kept in `%LOCALAPPDATA%\PowerMateBT\PowerMateBT.log`.

## Diagnostic tool

`src/PowerMateBT.Diag` connects to the paired PowerMate, lists every service and
characteristic it offers, subscribes to all notifications and prints the raw bytes as you use
the knob. Everything is also written to `powermate-log.txt` in the current folder.

```
dotnet run --project src/PowerMateBT.Diag
```

While it runs you can type:

| Command | Effect |
|---|---|
| `r <n>` | Read characteristic *n* |
| `w <n> <hex>` | Write bytes to characteristic *n*, e.g. `w 3 a1` |
| `q` | Quit |
| anything else | Written to the log as a note, to label what you're about to do |

## Protocol

Everything the app relies on, as measured with the diagnostic tool. The PowerMate exposes a
vendor GATT service `25598cf7-4240-40a6-9910-080f19f91ebc`:

| Characteristic | Type | Contents |
|---|---|---|
| `9cf53570-ddd9-47f3-ba63-09acefc60415` | Notify | Knob events, one byte (below) |
| `50f09cc9-fe1d-4c79-a962-b3a7cd3e5584` | Notify | Battery level in percent, every 30 s |
| `847d189e-86ee-4bd2-966f-800832b1259d` | Write | LED, one byte (below) |
| `c5cf8ae4-6988-409f-9ec4-f9daa9147d15` | Read/Write | Device name |

| Event byte | Meaning |
|---|---|
| `67` / `68` | Turn anticlockwise / clockwise |
| `69` / `70` | Turn anticlockwise / clockwise while pressed |
| `72`…`77` | Still held (ticks) |
| `65` | Released after a click or a press-and-turn (unreliable: sometimes late or duplicated) |
| `66` | Released after a long hold |

| LED byte | Effect |
|---|---|
| `00` | Off |
| `01`…`7e` | On for a moment (longer for higher values), then off; `7f` does nothing |
| `80` | Nothing |
| `a0` | Double pulse, then steady on |
| `a1`, `c0` | Keeps pulsing |
| `bf` | Steady bright on |
| `ff` | Short pulse |

There is no event for pressing down; the knob only reports what happened on release. When
idle the knob sleeps and is unreachable until it is touched.

## Credits

Earlier community work on the Bluetooth PowerMate:

- [gorlak/PowerMateTray](https://github.com/gorlak/PowerMateTray)
- [s7726/PowerMate](https://github.com/s7726/PowerMate)

## License

[MIT](LICENSE)
