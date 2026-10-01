// PowerMateBT diagnostic tool.
//
// Connects to a paired Griffin PowerMate Bluetooth, dumps every GATT service and
// characteristic it exposes, subscribes to everything that can notify, and logs
// the raw bytes. Also lets you read/write characteristics by hand so we can work
// out the LED protocol.
//
// Usage: PowerMateBT.Diag [name-filter]   (default filter: "PowerMate")

using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;

namespace PowerMateBT.Diag;

internal static class Program
{
    static readonly Dictionary<string, string> KnownUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1800"] = "Generic Access",
        ["1801"] = "Generic Attribute",
        ["180a"] = "Device Information",
        ["180f"] = "Battery Service",
        ["2a00"] = "Device Name",
        ["2a01"] = "Appearance",
        ["2a04"] = "Preferred Connection Parameters",
        ["2a05"] = "Service Changed",
        ["2a19"] = "Battery Level",
        ["2a23"] = "System ID",
        ["2a24"] = "Model Number",
        ["2a25"] = "Serial Number",
        ["2a26"] = "Firmware Revision",
        ["2a27"] = "Hardware Revision",
        ["2a28"] = "Software Revision",
        ["2a29"] = "Manufacturer Name",
        ["2a50"] = "PnP ID",
        ["25598cf7-4240-40a6-9910-080f19f91ebc"] = "PowerMate (Griffin vendor service)",
        ["f000ffc0-0451-4000-b000-000000000000"] = "TI OAD (firmware update)",
    };

    static readonly object LogLock = new();
    static StreamWriter? _log;

    // Keep references alive so notifications keep arriving.
    static BluetoothLEDevice? _device;
    static readonly List<GattDeviceService> Services = new();
    static readonly List<GattCharacteristic> Characteristics = new();

    static async Task<int> Main(string[] args)
    {
        var logPath = Path.Combine(Environment.CurrentDirectory, "powermate-log.txt");
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Log($"PowerMateBT diagnostic - logging to {logPath}");

        try
        {
            _device = await FindDeviceAsync(args.FirstOrDefault() ?? "PowerMate");
            if (_device is null)
                return 1;

            await DumpAndSubscribeAsync(_device);
            await CommandLoopAsync();
            await UnsubscribeAllAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            return 2;
        }
        finally
        {
            foreach (var s in Services) s.Dispose();
            _device?.Dispose();
            _log.Dispose();
        }
    }

    static async Task<BluetoothLEDevice?> FindDeviceAsync(string nameFilter)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector);

        Log($"Paired Bluetooth LE devices ({infos.Count}):");
        foreach (var i in infos)
            Log($"  {i.Name}");

        var match = infos.FirstOrDefault(i => i.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Log($"No paired device with a name containing '{nameFilter}'. Pair it in Windows Settings first.");
            return null;
        }

        var device = await BluetoothLEDevice.FromIdAsync(match.Id);
        if (device is null)
        {
            Log($"Could not open '{match.Name}'.");
            return null;
        }

        Log($"Opened '{device.Name}', address {FormatAddress(device.BluetoothAddress)}, status {device.ConnectionStatus}");
        device.ConnectionStatusChanged += (d, _) => Log($"*** Connection status: {d.ConnectionStatus}");
        return device;
    }

    static async Task DumpAndSubscribeAsync(BluetoothLEDevice device)
    {
        // The PowerMate sleeps when idle and reports Unreachable until it is touched, so keep retrying.
        GattDeviceServicesResult servicesResult;
        while (true)
        {
            servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status == GattCommunicationStatus.Success)
                break;

            Log($"Services not available yet ({servicesResult.Status}) - turn or press the knob to wake it. Retrying...");
            await Task.Delay(2000);
        }

        foreach (var service in servicesResult.Services)
        {
            Services.Add(service);
            Log("");
            Log($"SERVICE {service.Uuid}  {Describe(service.Uuid)}");

            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                Log($"  Failed to get characteristics: {charsResult.Status}");
                continue;
            }

            foreach (var c in charsResult.Characteristics)
            {
                int index = Characteristics.Count;
                Characteristics.Add(c);

                var description = string.IsNullOrEmpty(c.UserDescription) ? "" : $"  \"{c.UserDescription}\"";
                Log($"  [{index}] {c.Uuid}  {Describe(c.Uuid)}{description}");
                Log($"       properties: {c.CharacteristicProperties}");

                // Windows owns Service Changed (0x2A05) and refuses to let apps subscribe to it.
                if (ShortUuid(c.Uuid) == "2a05")
                {
                    Log("       (managed by Windows, skipped)");
                    continue;
                }

                try
                {
                    if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        await ReadAsync(index);

                    await SubscribeAsync(index);
                }
                catch (Exception ex)
                {
                    Log($"       error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Log("");
        Log("Now turn, press, hold and press-and-turn the knob. Events are logged below.");
    }

    static async Task SubscribeAsync(int index)
    {
        var c = Characteristics[index];
        GattClientCharacteristicConfigurationDescriptorValue cccd;
        if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Notify;
        else if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        else
            return;

        c.ValueChanged += OnValueChanged;
        var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
        Log($"       subscribed ({cccd}): {status}");
    }

    static async Task UnsubscribeAllAsync()
    {
        foreach (var c in Characteristics)
        {
            var props = c.CharacteristicProperties;
            if (!props.HasFlag(GattCharacteristicProperties.Notify) && !props.HasFlag(GattCharacteristicProperties.Indicate))
                continue;

            c.ValueChanged -= OnValueChanged;
            try
            {
                await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                // Device may already be gone; nothing to clean up.
            }
        }
    }

    static void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] bytes);
        int index = Characteristics.IndexOf(sender);
        Log($"{args.Timestamp.LocalDateTime:HH:mm:ss.fff}  [{index}] {ShortUuid(sender.Uuid)}  <-  {FormatBytes(bytes)}");
    }

    static async Task ReadAsync(int index)
    {
        var result = await Characteristics[index].ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
        {
            Log($"       read failed: {result.Status}");
            return;
        }

        CryptographicBuffer.CopyToByteArray(result.Value, out byte[] bytes);
        Log($"       value: {FormatBytes(bytes)}");
    }

    static async Task WriteAsync(int index, byte[] bytes)
    {
        var c = Characteristics[index];
        var option = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var result = await c.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(bytes), option);
        Log($"  write [{index}] {Convert.ToHexString(bytes)} ({option}): {result.Status}");
    }

    static async Task CommandLoopAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Commands:  r <n>         read characteristic n");
        Console.WriteLine("           w <n> <hex>   write bytes, e.g.  w 3 a1  or  w 3 01 ff");
        Console.WriteLine("           q             quit");
        Console.WriteLine("           anything else is written to the log as a note");
        Console.WriteLine();

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "q":
                        return;
                    case "r" when parts.Length == 2 && TryIndex(parts[1], out int r):
                        Log($"> {line}");
                        await ReadAsync(r);
                        break;
                    case "w" when parts.Length >= 3 && TryIndex(parts[1], out int w):
                        Log($"> {line}");
                        await WriteAsync(w, Convert.FromHexString(string.Concat(parts.Skip(2))));
                        break;
                    default:
                        // Anything else is a note, so you can label what you're about to do with the knob.
                        Log($"----- NOTE: {line}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"  error: {ex.Message}");
            }
        }
    }

    static bool TryIndex(string s, out int index) =>
        int.TryParse(s, out index) && index >= 0 && index < Characteristics.Count;

    static string Describe(Guid uuid)
    {
        var full = uuid.ToString();
        return KnownUuids.TryGetValue(full, out var name) || KnownUuids.TryGetValue(ShortUuid(uuid), out name)
            ? $"({name})"
            : "";
    }

    // Standard Bluetooth UUIDs are 0000xxxx-0000-1000-8000-00805f9b34fb; show just xxxx for those.
    static string ShortUuid(Guid uuid)
    {
        var s = uuid.ToString();
        return s.EndsWith("-0000-1000-8000-00805f9b34fb") && s.StartsWith("0000") ? s.Substring(4, 4) : s;
    }

    static string FormatBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(empty)";

        var hex = string.Join(' ', bytes.Select(b => b.ToString("X2")));
        var ascii = new string(bytes.Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.').ToArray());
        return $"{hex}   |{ascii}|";
    }

    static string FormatAddress(ulong address)
    {
        var sb = new StringBuilder();
        for (int shift = 40; shift >= 0; shift -= 8)
        {
            if (sb.Length > 0) sb.Append(':');
            sb.Append(((address >> shift) & 0xff).ToString("X2"));
        }
        return sb.ToString();
    }

    static void Log(string message)
    {
        lock (LogLock)
        {
            Console.WriteLine(message);
            _log?.WriteLine(message);
        }
    }
}
