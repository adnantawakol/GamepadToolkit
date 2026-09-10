using GamepadToolkit.Core.Firmware;
using HidSharp;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace GamepadToolkit.Core.Devices;

public static class DeviceScanner
{
    private const string BluetoothHidServiceUuid = "{00001124-0000-1000-8000-00805f9b34fb}";

    public static IReadOnlyList<GamepadInfo> Scan(bool controllersOnly = true)
    {
        var found = new List<GamepadInfo>();

        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            GamepadInfo info;
            try
            {
                info = Describe(dev);
            }
            catch
            {
                continue;
            }

            if (controllersOnly && !info.IsGameController && !info.HasViaInterface)
                continue;

            found.Add(info);
        }

        return found
            .OrderByDescending(d => d.IsGameController)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GamepadInfo Describe(HidDevice dev)
    {
        var (usagePage, usage, raw) = ReadUsage(dev);

        var info = new GamepadInfo
        {
            DevicePath = dev.DevicePath,
            VendorId = dev.VendorID,
            ProductId = dev.ProductID,
            ReleaseNumber = dev.ReleaseNumberBcd,
            ProductName = Safe(dev.GetProductName),
            Manufacturer = Safe(dev.GetManufacturer),
            SerialNumber = Safe(dev.GetSerialNumber),
            InstanceId = ToInstanceId(dev.DevicePath),
            Transport = DetectTransport(dev.DevicePath),
            UsagePage = usagePage,
            Usage = usage,
            MaxInputReportLength = SafeInt(dev.GetMaxInputReportLength),
            MaxOutputReportLength = SafeInt(dev.GetMaxOutputReportLength),
            MaxFeatureReportLength = SafeInt(dev.GetMaxFeatureReportLength),
            RawReportDescriptor = raw
        };

        info.IsVirtual = IsSoftwareEnumerated(info.InstanceId);

        var profile = KnownDevices.Lookup(info.VendorId, info.ProductId);
        if (profile is { } p)
        {
            info.KnownName = p.Name;
            info.FirmwareAccess = p.Access;
            info.AccessNote = p.Note;
        }
        else if (info.HasViaInterface)
        {
            info.FirmwareAccess = FirmwareAccess.ConfigOnly;
            info.AccessNote = "Exposes a VIA raw-HID interface — keymap in EEPROM is readable and writable.";
        }
        else
        {
            info.FirmwareAccess = FirmwareAccess.Unknown;
            info.AccessNote = "Not in the known-device table. Probe to determine what this device exposes.";
        }

        return info;
    }

    private static (ushort UsagePage, ushort Usage, byte[] Raw) ReadUsage(HidDevice dev)
    {
        byte[] raw;
        try
        {
            raw = dev.GetRawReportDescriptor();
        }
        catch
        {
            raw = [];
        }

        try
        {
            var descriptor = dev.GetReportDescriptor();
            var all = descriptor.DeviceItems
                .SelectMany(i => i.Usages.GetAllValues())
                .Select(u => ((ushort)(u >> 16), (ushort)(u & 0xFFFF)))
                .ToList();

            if (all.Count == 0)
                return (0, 0, raw);

            // A composite device can expose several collections; the controller one wins.
            foreach (var candidate in all)
            {
                if (candidate is { Item1: 0x01, Item2: 0x05 } or { Item1: 0x01, Item2: 0x04 })
                    return (candidate.Item1, candidate.Item2, raw);
            }

            foreach (var candidate in all)
            {
                if (candidate is { Item1: 0xFF60, Item2: 0x61 })
                    return (candidate.Item1, candidate.Item2, raw);
            }

            return (all[0].Item1, all[0].Item2, raw);
        }
        catch
        {
            return (0, 0, raw);
        }
    }

    private static DeviceTransport DetectTransport(string devicePath)
    {
        var p = devicePath.ToLowerInvariant();

        if (p.Contains(BluetoothHidServiceUuid))
            return DeviceTransport.BluetoothClassic;

        if (p.Contains("bthledevice") || p.Contains("_dev_vid&"))
            return DeviceTransport.BluetoothLe;

        if (p.Contains("bthenum") || p.Contains("bthhfenum"))
            return DeviceTransport.BluetoothClassic;

        if (p.Contains("hid#vid_") || p.Contains("usb#vid_"))
            return DeviceTransport.Usb;

        return DeviceTransport.Unknown;
    }

    /// <summary>Turns an interface path into the PnP instance id Device Manager shows.</summary>
    private static string? ToInstanceId(string devicePath)
    {
        if (!devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return null;

        var parts = devicePath[4..].Split('#');
        return parts.Length < 3 ? null : string.Join('\\', parts.Take(3)).ToUpperInvariant();
    }

    /// <summary>
    /// A ViGEm virtual pad hangs off ROOT\SYSTEM rather than a physical bus. Walking up to a
    /// ROOT\ parent is what separates it from a real controller sharing the same VID/PID.
    /// </summary>
    private static bool IsSoftwareEnumerated(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        var current = instanceId;

        for (var depth = 0; depth < 5; depth++)
        {
            string? parent;
            try
            {
                parent = PnPDevice.GetDeviceByInstanceId(current)
                    .GetProperty<string>(DevicePropertyKey.Device_Parent);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parent))
                return false;

            if (parent.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase))
                return true;

            current = parent;
        }

        return false;
    }

    private static string? Safe(Func<string> get)
    {
        try
        {
            var v = get();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static int SafeInt(Func<int> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return 0;
        }
    }
}
