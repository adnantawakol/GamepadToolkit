using GamepadToolkit.Core.Firmware;

namespace GamepadToolkit.Core.Devices;

public sealed class GamepadInfo
{
    public required string DevicePath { get; init; }
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public int ReleaseNumber { get; init; }

    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? InstanceId { get; init; }

    public DeviceTransport Transport { get; init; }

    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }

    public int MaxInputReportLength { get; init; }
    public int MaxOutputReportLength { get; init; }
    public int MaxFeatureReportLength { get; init; }

    public byte[] RawReportDescriptor { get; init; } = [];

    /// <summary>True when the HID usage marks this as a joystick (0x04) or gamepad (0x05).</summary>
    public bool IsGameController => UsagePage == 0x01 && Usage is 0x04 or 0x05;

    /// <summary>QMK/VIA raw console interface: usage page 0xFF60, usage 0x61.</summary>
    public bool HasViaInterface => UsagePage == 0xFF60 && Usage == 0x61;

    /// <summary>Windows appends "ig_" to the interface path of XInput-capable devices.</summary>
    public bool IsXInputCapable =>
        DevicePath.Contains("ig_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for software-enumerated pads such as ViGEm's virtual controller, which sit under a
    /// ROOT\ parent rather than a real bus. Hiding one of these is always a mistake — it is the
    /// device games are supposed to see.
    /// </summary>
    public bool IsVirtual { get; set; }

    public string? KnownName { get; set; }
    public FirmwareAccess FirmwareAccess { get; set; } = FirmwareAccess.Unknown;
    public string? AccessNote { get; set; }
    public string? FirmwareVersion { get; set; }

    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public string DisplayName
    {
        get
        {
            var name = KnownName
                       ?? (string.IsNullOrWhiteSpace(ProductName) ? null : ProductName)
                       ?? $"Unknown HID device ({VidPid})";

            return IsVirtual ? $"{name} (virtual)" : name;
        }
    }

    public override string ToString() => $"{DisplayName} [{VidPid}, {Transport}]";
}
