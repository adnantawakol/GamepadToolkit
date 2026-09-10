using GamepadToolkit.Core.Devices;
using GamepadToolkit.Core.Firmware.Backends;

namespace GamepadToolkit.Core.Firmware;

public sealed class FirmwareService
{
    // Most specific first; UnknownDeviceBackend accepts anything, so it stays last.
    private readonly IFirmwareBackend[] _backends =
    [
        new ViaBackend(),
        new LockedDeviceBackend(),
        new UnknownDeviceBackend()
    ];

    public IFirmwareBackend Resolve(GamepadInfo device) =>
        _backends.First(b => b.CanHandle(device));

    public FirmwareProbeResult Probe(GamepadInfo device) => Resolve(device).Probe(device);

    public Task<FirmwareImage> ReadAsync(GamepadInfo device, IProgress<double>? progress = null, CancellationToken ct = default) =>
        Resolve(device).ReadAsync(device, progress, ct);

    public Task WriteAsync(GamepadInfo device, FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default) =>
        Resolve(device).WriteAsync(device, image, progress, ct);

    /// <summary>UF2 boards enumerate as mass storage rather than HID, so they are found separately.</summary>
    public IReadOnlyList<Uf2Volume> DiscoverBootloaders() => Uf2Volume.Discover();
}
