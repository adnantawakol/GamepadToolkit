namespace GamepadToolkit.Core.Firmware;

/// <summary>What a device physically permits, independent of whether we implement it.</summary>
public enum FirmwareAccess
{
    Unknown,

    /// <summary>No firmware or config interface is exposed over any transport.</summary>
    None,

    /// <summary>Signed + readout-protected. Dumping and unsigned flashing are both impossible.</summary>
    Locked,

    /// <summary>Updatable, but only through the vendor's own signed updater.</summary>
    VendorToolOnly,

    /// <summary>Firmware itself is closed, but mappings/settings live in readable, writable NVRAM.</summary>
    ConfigOnly,

    /// <summary>Flashable, but the image cannot be read back (readout protection is set).</summary>
    WriteOnly,

    /// <summary>Genuine dump and flash of the full firmware image.</summary>
    ReadWrite
}

public static class FirmwareAccessInfo
{
    public static bool CanRead(this FirmwareAccess a) =>
        a is FirmwareAccess.ReadWrite or FirmwareAccess.ConfigOnly;

    public static bool CanWrite(this FirmwareAccess a) =>
        a is FirmwareAccess.ReadWrite or FirmwareAccess.ConfigOnly or FirmwareAccess.WriteOnly;

    public static string Describe(this FirmwareAccess a) => a switch
    {
        FirmwareAccess.None => "No firmware interface exposed",
        FirmwareAccess.Locked => "Locked — signed firmware, readout protected",
        FirmwareAccess.VendorToolOnly => "Vendor updater only — signed images",
        FirmwareAccess.ConfigOnly => "Config/mapping memory is readable and writable",
        FirmwareAccess.WriteOnly => "Flashable, but cannot be dumped",
        FirmwareAccess.ReadWrite => "Full dump and flash supported",
        _ => "Not yet probed"
    };
}
