using GamepadToolkit.Core.Devices;

namespace GamepadToolkit.Core.Firmware;

public sealed record FirmwareProbeResult(
    FirmwareAccess Access,
    string Backend,
    string Summary,
    string? Detail = null,
    string? Version = null)
{
    public bool CanRead => Access.CanRead();
    public bool CanWrite => Access.CanWrite();
}

public interface IFirmwareBackend
{
    string Name { get; }

    bool CanHandle(GamepadInfo device);

    FirmwareProbeResult Probe(GamepadInfo device);

    /// <summary>Throws <see cref="NotSupportedException"/> when the device forbids readback.</summary>
    Task<FirmwareImage> ReadAsync(GamepadInfo device, IProgress<double>? progress = null, CancellationToken ct = default);

    Task WriteAsync(GamepadInfo device, FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default);
}
