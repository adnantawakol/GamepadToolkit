using GamepadToolkit.Core.Firmware;

namespace GamepadToolkit.Core.Devices;

public readonly record struct DeviceProfile(string Name, FirmwareAccess Access, string Note);

public static class KnownDevices
{
    private const string XboxNote =
        "Xbox controller firmware is signed by Microsoft and the MCU's readout-protection fuses are set. " +
        "The update channel accepts only signed, encrypted images and never reads back. Remap via the virtual-pad layer instead.";

    private const string PlayStationNote =
        "PlayStation controller firmware is signed and updated only by the console or Sony's updater. No dump path exists.";

    private const string NintendoNote =
        "Nintendo controller firmware is signed and updated only by the console. No dump path exists.";

    private static readonly Dictionary<(int Vid, int Pid), DeviceProfile> Table = new()
    {
        // Microsoft
        [(0x045E, 0x028E)] = new("Xbox 360 Controller", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x028F)] = new("Xbox 360 Wireless Receiver", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x02D1)] = new("Xbox One Controller (2013)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x02DD)] = new("Xbox One Controller (2015)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x02E0)] = new("Xbox One S Controller (Bluetooth)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x02E3)] = new("Xbox Elite Controller", FirmwareAccess.VendorToolOnly,
            "Elite pads store remap profiles on-device, but only the Xbox Accessories app can write them — the protocol is proprietary and the firmware itself stays signed."),
        [(0x045E, 0x02EA)] = new("Xbox One S Controller (USB)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x02FD)] = new("Xbox One S Controller (Bluetooth)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x0B00)] = new("Xbox Elite Series 2 (USB)", FirmwareAccess.VendorToolOnly,
            "Elite Series 2 profiles live in controller NVRAM but are written only by the Xbox Accessories app. Firmware is signed."),
        [(0x045E, 0x0B05)] = new("Xbox Elite Series 2 (Bluetooth)", FirmwareAccess.VendorToolOnly, XboxNote),
        [(0x045E, 0x0B12)] = new("Xbox Series X|S Controller (USB)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x0B13)] = new("Xbox Series X|S Controller (Bluetooth)", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x0B20)] = new("Xbox Series X|S Controller", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x0B21)] = new("Xbox Adaptive Controller", FirmwareAccess.Locked, XboxNote),
        [(0x045E, 0x0B22)] = new("Xbox Elite Series 2 (updated)", FirmwareAccess.VendorToolOnly, XboxNote),

        // Sony
        [(0x054C, 0x05C4)] = new("DualShock 4 (v1)", FirmwareAccess.Locked, PlayStationNote),
        [(0x054C, 0x09CC)] = new("DualShock 4 (v2)", FirmwareAccess.Locked, PlayStationNote),
        [(0x054C, 0x0BA0)] = new("DualShock 4 USB Adapter", FirmwareAccess.Locked, PlayStationNote),
        [(0x054C, 0x0CE6)] = new("DualSense", FirmwareAccess.Locked, PlayStationNote),
        [(0x054C, 0x0DF2)] = new("DualSense Edge", FirmwareAccess.Locked, PlayStationNote),

        // Nintendo
        [(0x057E, 0x2006)] = new("Joy-Con (L)", FirmwareAccess.Locked, NintendoNote),
        [(0x057E, 0x2007)] = new("Joy-Con (R)", FirmwareAccess.Locked, NintendoNote),
        [(0x057E, 0x2009)] = new("Switch Pro Controller", FirmwareAccess.Locked, NintendoNote),
        [(0x057E, 0x200E)] = new("Joy-Con Charging Grip", FirmwareAccess.Locked, NintendoNote),

        // Logitech
        // The mode switch on these pads changes the PID: C21x in DirectInput, C21D-F in XInput.
        [(0x046D, 0xC216)] = new("Logitech F310 (DirectInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),
        [(0x046D, 0xC218)] = new("Logitech F510 (DirectInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),
        [(0x046D, 0xC219)] = new("Logitech F710 (DirectInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),
        [(0x046D, 0xC21D)] = new("Logitech F310 (XInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),
        [(0x046D, 0xC21E)] = new("Logitech F510 (XInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),
        [(0x046D, 0xC21F)] = new("Logitech F710 (XInput)", FirmwareAccess.None, "Fixed-function controller with no field-updatable firmware."),

        // Open hardware running as a HID gamepad. The bootloader itself enumerates as mass
        // storage, not HID, so it is discovered separately by Uf2Volume.Discover().
        [(0x2E8A, 0x000A)] = new("Raspberry Pi Pico (application mode)", FirmwareAccess.ReadWrite,
            "Hold BOOTSEL while replugging to expose the UF2 mass-storage bootloader, which supports a full dump and reflash."),
        [(0xFEED, 0x0000)] = new("QMK Device", FirmwareAccess.ConfigOnly,
            "QMK firmware exposes a VIA raw-HID interface for reading and writing the keymap in EEPROM."),
    };

    private static readonly Dictionary<int, string> Vendors = new()
    {
        [0x045E] = "Microsoft",
        [0x054C] = "Sony",
        [0x057E] = "Nintendo",
        [0x046D] = "Logitech",
        [0x2DC8] = "8BitDo",
        [0x0F0D] = "Hori",
        [0x1532] = "Razer",
        [0x20D6] = "PowerA",
        [0x146B] = "Nacon",
        [0x044F] = "Thrustmaster",
        [0x28DE] = "Valve",
        [0x2E8A] = "Raspberry Pi",
        [0x0483] = "STMicroelectronics",
        [0x239A] = "Adafruit",
        [0x03EB] = "Atmel / Microchip",
        [0x1209] = "pid.codes (open hardware)",
        [0xFEED] = "QMK",
        [0x320F] = "Evision / generic HID",
    };

    public static DeviceProfile? Lookup(int vid, int pid)
    {
        if (Table.TryGetValue((vid, pid), out var exact))
            return exact;

        // 8BitDo ships dozens of PIDs that all share one updater.
        if (vid == 0x2DC8)
            return new DeviceProfile("8BitDo Controller", FirmwareAccess.VendorToolOnly,
                "8BitDo distributes signed firmware through its own Upgrade Tool. Images are not readable from the device.");

        if (vid == 0xFEED)
            return new DeviceProfile("QMK Device", FirmwareAccess.ConfigOnly,
                "QMK firmware exposes a VIA raw-HID interface for reading and writing the keymap in EEPROM.");

        return null;
    }

    public static string VendorName(int vid) =>
        Vendors.TryGetValue(vid, out var n) ? n : $"Unknown vendor (0x{vid:X4})";
}
