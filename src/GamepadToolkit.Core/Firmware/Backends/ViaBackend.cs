using GamepadToolkit.Core.Devices;
using HidSharp;

namespace GamepadToolkit.Core.Firmware.Backends;

/// <summary>
/// Reads and writes the dynamic keymap of a QMK/VIA device over its raw-HID interface
/// (usage page 0xFF60, usage 0x61). The firmware binary stays closed, but the mapping
/// stored in EEPROM is genuinely dumpable and restorable.
/// </summary>
public sealed class ViaBackend : IFirmwareBackend
{
    private const byte CmdGetProtocolVersion = 0x01;
    private const byte CmdGetLayerCount = 0x11;
    private const byte CmdGetBuffer = 0x12;
    private const byte CmdSetBuffer = 0x13;

    private const int PayloadSize = 32;
    private const int ChunkSize = 28;

    /// <summary>VIA cannot report matrix dimensions, so the dump length is bounded explicitly.</summary>
    public int KeymapBufferSize { get; set; } = 1024;

    public string Name => "QMK / VIA dynamic keymap";

    public bool CanHandle(GamepadInfo device) => device.HasViaInterface;

    public FirmwareProbeResult Probe(GamepadInfo device)
    {
        try
        {
            using var session = Open(device);
            var version = session.Send(CmdGetProtocolVersion);
            var protocol = (version[1] << 8) | version[2];
            var layers = session.Send(CmdGetLayerCount)[1];

            return new FirmwareProbeResult(
                FirmwareAccess.ConfigOnly,
                Name,
                $"VIA interface live — protocol {protocol}, {layers} keymap layers",
                $"The dynamic keymap in EEPROM can be dumped and restored. Backups capture {KeymapBufferSize} bytes.\n" +
                "The firmware image itself is not exposed by VIA; reflashing that needs the board's bootloader (usually UF2 or DFU).",
                protocol.ToString());
        }
        catch (Exception ex)
        {
            return new FirmwareProbeResult(
                FirmwareAccess.Unknown,
                Name,
                "VIA interface present but did not answer",
                $"The device advertises usage page 0xFF60 but the handshake failed: {ex.Message}\n" +
                "Another application may hold the interface open, or VIA support may be disabled in this firmware build.");
        }
    }

    public Task<FirmwareImage> ReadAsync(GamepadInfo device, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var session = Open(device);
        var buffer = new byte[KeymapBufferSize];

        for (var offset = 0; offset < buffer.Length; offset += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var size = Math.Min(ChunkSize, buffer.Length - offset);
            var response = session.Send(CmdGetBuffer, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)size);

            // Response echoes cmd + 3 argument bytes, so payload starts at index 4.
            Array.Copy(response, 4, buffer, offset, size);
            progress?.Report((double)(offset + size) / buffer.Length);
        }

        return Task.FromResult(new FirmwareImage
        {
            Data = buffer,
            SourceDevice = device.DisplayName,
            VidPid = device.VidPid,
            Format = "bin",
            Backend = Name
        });
    }

    public Task WriteAsync(GamepadInfo device, FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var session = Open(device);
        var data = image.Data;

        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var size = Math.Min(ChunkSize, data.Length - offset);
            var args = new byte[3 + size];
            args[0] = (byte)(offset >> 8);
            args[1] = (byte)(offset & 0xFF);
            args[2] = (byte)size;
            Array.Copy(data, offset, args, 3, size);

            session.Send(CmdSetBuffer, args);
            progress?.Report((double)(offset + size) / data.Length);
        }

        return Task.CompletedTask;
    }

    private static ViaSession Open(GamepadInfo device)
    {
        var hid = DeviceList.Local.GetHidDevices()
                      .FirstOrDefault(d => string.Equals(d.DevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"{device.DisplayName} is no longer connected.");

        return new ViaSession(hid);
    }

    private sealed class ViaSession : IDisposable
    {
        private readonly HidStream _stream;
        private readonly int _inputLength;

        public ViaSession(HidDevice device)
        {
            _stream = device.Open();
            _stream.ReadTimeout = 2000;
            _stream.WriteTimeout = 2000;
            _inputLength = Math.Max(device.GetMaxInputReportLength(), PayloadSize + 1);
        }

        public byte[] Send(byte command, params byte[] args)
        {
            // Windows raw HID always carries a leading report-ID byte, which VIA leaves at zero.
            var request = new byte[PayloadSize + 1];
            request[1] = command;
            Array.Copy(args, 0, request, 2, Math.Min(args.Length, PayloadSize - 1));

            _stream.Write(request, 0, request.Length);

            var response = new byte[_inputLength];
            _stream.Read(response, 0, response.Length);

            // Strip the report-ID byte so index 0 is the echoed command.
            return response[1..];
        }

        public void Dispose() => _stream.Dispose();
    }
}
