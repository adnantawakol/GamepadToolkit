using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamepadToolkit.Core.Firmware;

/// <summary>A captured firmware or configuration blob plus the metadata needed to verify a restore.</summary>
public sealed class FirmwareImage
{
    public required byte[] Data { get; init; }
    public required string SourceDevice { get; init; }
    public string? VidPid { get; init; }
    public string? DeviceVersion { get; init; }

    /// <summary>bin, uf2, or via-json.</summary>
    public string Format { get; init; } = "bin";

    public string Backend { get; init; } = "unknown";
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Data)).ToLowerInvariant();

    public int Length => Data.Length;

    private static readonly JsonSerializerOptions MetaOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Writes the payload and a sidecar manifest beside it.</summary>
    public string Save(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(full, Data);

        var manifest = new FirmwareManifest
        {
            SourceDevice = SourceDevice,
            VidPid = VidPid,
            DeviceVersion = DeviceVersion,
            Format = Format,
            Backend = Backend,
            CapturedUtc = CapturedUtc,
            Length = Length,
            Sha256 = Sha256,
            PayloadFile = Path.GetFileName(full)
        };

        var metaPath = full + ".json";
        File.WriteAllText(metaPath, JsonSerializer.Serialize(manifest, MetaOptions));
        return metaPath;
    }

    public static FirmwareImage Load(string path)
    {
        var full = Path.GetFullPath(path);
        var data = File.ReadAllBytes(full);
        var metaPath = full + ".json";

        if (!File.Exists(metaPath))
        {
            return new FirmwareImage
            {
                Data = data,
                SourceDevice = "unknown (no manifest alongside payload)",
                Format = Path.GetExtension(full).TrimStart('.').ToLowerInvariant()
            };
        }

        var manifest = JsonSerializer.Deserialize<FirmwareManifest>(File.ReadAllText(metaPath), MetaOptions)
                       ?? throw new InvalidDataException($"Manifest at {metaPath} is not valid JSON.");

        var image = new FirmwareImage
        {
            Data = data,
            SourceDevice = manifest.SourceDevice ?? "unknown",
            VidPid = manifest.VidPid,
            DeviceVersion = manifest.DeviceVersion,
            Format = manifest.Format ?? "bin",
            Backend = manifest.Backend ?? "unknown",
            CapturedUtc = manifest.CapturedUtc
        };

        if (!string.IsNullOrEmpty(manifest.Sha256) &&
            !string.Equals(manifest.Sha256, image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Payload does not match its manifest hash — the backup at {full} is corrupt. " +
                $"Expected {manifest.Sha256}, got {image.Sha256}.");
        }

        return image;
    }

    private sealed class FirmwareManifest
    {
        public string? SourceDevice { get; set; }
        public string? VidPid { get; set; }
        public string? DeviceVersion { get; set; }
        public string? Format { get; set; }
        public string? Backend { get; set; }
        public DateTimeOffset CapturedUtc { get; set; }
        public int Length { get; set; }
        public string? Sha256 { get; set; }
        public string? PayloadFile { get; set; }
    }
}
