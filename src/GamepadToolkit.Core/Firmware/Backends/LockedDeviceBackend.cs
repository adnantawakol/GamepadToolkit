using GamepadToolkit.Core.Devices;

namespace GamepadToolkit.Core.Firmware.Backends;

/// <summary>
/// Handles pads whose firmware is vendor-signed. It never pretends a dump is possible —
/// its job is to say precisely why not, and what to do instead.
/// </summary>
public sealed class LockedDeviceBackend : IFirmwareBackend
{
    public string Name => "Vendor-signed firmware";

    public bool CanHandle(GamepadInfo device) =>
        device.FirmwareAccess is FirmwareAccess.Locked or FirmwareAccess.VendorToolOnly or FirmwareAccess.None;

    public FirmwareProbeResult Probe(GamepadInfo device)
    {
        var detail = device.AccessNote ?? "This device does not expose a firmware interface.";

        if (device is { Transport: DeviceTransport.BluetoothClassic, MaxOutputReportLength: 0, MaxFeatureReportLength: 0 })
        {
            detail += "\n\nConfirmed on this connection: the device advertises no output reports and no feature reports, " +
                      "so there is no channel of any kind to carry a firmware command over Bluetooth. " +
                      "Connect by USB if you want to inspect what the wired interface exposes.";
        }

        var summary = device.FirmwareAccess == FirmwareAccess.VendorToolOnly
            ? "Updatable only through the vendor's signed updater"
            : "Firmware cannot be read or replaced";

        return new FirmwareProbeResult(device.FirmwareAccess, Name, summary, detail);
    }

    public Task<FirmwareImage> ReadAsync(GamepadInfo device, IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{device.DisplayName} does not support firmware readback. {device.AccessNote} " +
            "Use Backup Profile to save your button mapping instead — that is portable and restorable.");

    public Task WriteAsync(GamepadInfo device, FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{device.DisplayName} rejects unsigned firmware — its bootloader verifies a vendor signature before flashing. " +
            "Remapping through the virtual-pad layer achieves the same result without touching firmware.");
}

/// <summary>Fallback for devices absent from the known-device table.</summary>
public sealed class UnknownDeviceBackend : IFirmwareBackend
{
    public string Name => "Unrecognised device";

    public bool CanHandle(GamepadInfo device) => true;

    public FirmwareProbeResult Probe(GamepadInfo device)
    {
        var hasChannel = device.MaxFeatureReportLength > 0 || device.MaxOutputReportLength > 0;

        var detail = hasChannel
            ? $"This device exposes {device.MaxOutputReportLength}-byte output reports and " +
              $"{device.MaxFeatureReportLength}-byte feature reports, so a vendor command channel may exist. " +
              "Identifying it requires reverse-engineering the vendor protocol — no generic method can do this safely."
            : "This device exposes no output or feature reports, so it has no channel that could carry firmware commands.";

        return new FirmwareProbeResult(
            hasChannel ? FirmwareAccess.Unknown : FirmwareAccess.None,
            Name,
            hasChannel ? "Unknown — a vendor channel exists but its protocol is undocumented" : "No firmware interface exposed",
            detail);
    }

    public Task<FirmwareImage> ReadAsync(GamepadInfo device, IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"No known firmware protocol for {device.VidPid}. Reading would require reverse-engineering this vendor's command set.");

    public Task WriteAsync(GamepadInfo device, FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"No known firmware protocol for {device.VidPid}. Blind writes to an unknown command channel risk bricking the device.");
}
