using System.Buffers.Binary;

namespace GamepadToolkit.Core.Firmware.Backends;

/// <summary>
/// A board sitting in its UF2 mass-storage bootloader (RP2040 in BOOTSEL, Adafruit nRF/SAMD, and friends).
/// These expose the full flash as CURRENT.UF2 and accept a dropped .uf2 to reflash — real dump and real write.
/// </summary>
public sealed class Uf2Volume
{
    private const uint MagicStart0 = 0x0A324655;
    private const uint MagicStart1 = 0x9E5D5157;
    private const uint MagicEnd = 0x0AB16F30;
    private const int BlockSize = 512;

    public required string Root { get; init; }
    public string? BoardId { get; init; }
    public string? Model { get; init; }
    public string? BootloaderVersion { get; init; }
    public long FreeBytes { get; init; }

    public string DisplayName => Model ?? BoardId ?? $"UF2 bootloader ({Root})";

    private string CurrentImagePath => Path.Combine(Root, "CURRENT.UF2");

    public bool HasReadableImage => File.Exists(CurrentImagePath);

    public static IReadOnlyList<Uf2Volume> Discover()
    {
        var volumes = new List<Uf2Volume>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            string info;
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Removable)
                    continue;

                var infoPath = Path.Combine(drive.RootDirectory.FullName, "INFO_UF2.TXT");
                if (!File.Exists(infoPath))
                    continue;

                info = File.ReadAllText(infoPath);
            }
            catch
            {
                continue;
            }

            volumes.Add(Parse(drive, info));
        }

        return volumes;
    }

    private static Uf2Volume Parse(DriveInfo drive, string info)
    {
        string? Field(string prefix) => info
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..]
            .Trim();

        var version = info
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("UF2 Bootloader", StringComparison.OrdinalIgnoreCase));

        return new Uf2Volume
        {
            Root = drive.RootDirectory.FullName,
            Model = Field("Model:"),
            BoardId = Field("Board-ID:"),
            BootloaderVersion = version,
            FreeBytes = drive.AvailableFreeSpace
        };
    }

    public FirmwareImage Read(CancellationToken ct = default)
    {
        if (!HasReadableImage)
        {
            throw new NotSupportedException(
                $"{DisplayName} does not publish CURRENT.UF2, so this bootloader is write-only. " +
                "Flashing will still work, but the existing image cannot be backed up.");
        }

        ct.ThrowIfCancellationRequested();

        return new FirmwareImage
        {
            Data = File.ReadAllBytes(CurrentImagePath),
            SourceDevice = DisplayName,
            DeviceVersion = BootloaderVersion,
            Format = "uf2",
            Backend = "UF2 mass-storage bootloader"
        };
    }

    public void Flash(FirmwareImage image, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Validate(image.Data);

        if (image.Data.LongLength > FreeBytes)
        {
            throw new IOException(
                $"Image is {image.Data.LongLength:N0} bytes but the bootloader volume reports only {FreeBytes:N0} bytes free.");
        }

        var target = Path.Combine(Root, "firmware.uf2");

        using var source = new MemoryStream(image.Data);
        using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize);

        var buffer = new byte[BlockSize];
        long written = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            written += read;
            progress?.Report((double)written / image.Data.LongLength);
        }

        destination.Flush(true);
    }

    /// <summary>Rejects anything that is not a well-formed UF2 before it reaches the board.</summary>
    public static void Validate(byte[] data)
    {
        if (data.Length == 0 || data.Length % BlockSize != 0)
        {
            throw new InvalidDataException(
                $"A UF2 image must be a whole number of 512-byte blocks; this file is {data.Length:N0} bytes. Refusing to flash it.");
        }

        var blocks = data.Length / BlockSize;

        for (var i = 0; i < blocks; i++)
        {
            var block = data.AsSpan(i * BlockSize, BlockSize);

            if (BinaryPrimitives.ReadUInt32LittleEndian(block) != MagicStart0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) != MagicStart1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(block[508..]) != MagicEnd)
            {
                throw new InvalidDataException(
                    $"Block {i} of {blocks} is missing its UF2 magic numbers. This file is not a valid UF2 image. Refusing to flash it.");
            }
        }
    }
}
