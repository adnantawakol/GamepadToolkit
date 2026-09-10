using System.Text.Json;
using System.Text.Json.Serialization;
using GamepadToolkit.Core.Input;

namespace GamepadToolkit.Core.Remap;

/// <summary>
/// A portable remap definition. This is the import/export unit — the practical stand-in for
/// "editing firmware" on controllers whose firmware is signed and unreadable.
/// </summary>
public sealed class MappingProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public string? Description { get; set; }
    public string? TargetVidPid { get; set; }
    public string? TargetDeviceName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<GamepadButton, GamepadButton> Buttons { get; set; } = new();
    public Dictionary<GamepadAxis, GamepadAxis> Axes { get; set; } = new();

    public bool InvertLeftStickY { get; set; }
    public bool InvertRightStickY { get; set; }

    /// <summary>Radial deadzone in raw stick units (0–32767). 0 disables.</summary>
    public int LeftStickDeadzone { get; set; }

    public int RightStickDeadzone { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static MappingProfile Identity(string name = "Passthrough") => new() { Name = name };

    public static MappingProfile SwapXY() => new()
    {
        Name = "Swap X / Y",
        Description = "Exchanges the X and Y face buttons.",
        Buttons =
        {
            [GamepadButton.X] = GamepadButton.Y,
            [GamepadButton.Y] = GamepadButton.X
        }
    };

    public static MappingProfile SwapFaceButtons() => new()
    {
        Name = "Swap X/Y and A/B",
        Description = "Exchanges X with Y and A with B together — the full Nintendo-style face layout.",
        Buttons =
        {
            [GamepadButton.X] = GamepadButton.Y,
            [GamepadButton.Y] = GamepadButton.X,
            [GamepadButton.A] = GamepadButton.B,
            [GamepadButton.B] = GamepadButton.A
        }
    };

    public static MappingProfile SwapAB() => new()
    {
        Name = "Swap A / B",
        Description = "Exchanges the A and B face buttons (Nintendo-style layout).",
        Buttons =
        {
            [GamepadButton.A] = GamepadButton.B,
            [GamepadButton.B] = GamepadButton.A
        }
    };

    public GamepadState Apply(in GamepadState input)
    {
        var output = new GamepadState();

        foreach (var source in Enum.GetValues<GamepadButton>())
        {
            if (!input.IsPressed(source))
                continue;

            var destination = Buttons.TryGetValue(source, out var mapped) ? mapped : source;
            output.Set(destination, true);
        }

        foreach (var source in Enum.GetValues<GamepadAxis>())
        {
            var destination = Axes.TryGetValue(source, out var mapped) ? mapped : source;
            var raw = source is GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger
                ? GamepadState.FromTrigger((byte)input.GetAxis(source))
                : input.GetAxis(source);

            output.SetAxis(destination, raw);
        }

        ApplyDeadzone(ref output);

        if (InvertLeftStickY) output.LeftStickY = Negate(output.LeftStickY);
        if (InvertRightStickY) output.RightStickY = Negate(output.RightStickY);

        return output;
    }

    private void ApplyDeadzone(ref GamepadState state)
    {
        if (LeftStickDeadzone > 0)
            Deadzone(ref state.LeftStickX, ref state.LeftStickY, LeftStickDeadzone);

        if (RightStickDeadzone > 0)
            Deadzone(ref state.RightStickX, ref state.RightStickY, RightStickDeadzone);
    }

    private static void Deadzone(ref short x, ref short y, int threshold)
    {
        double dx = x, dy = y;
        var magnitude = Math.Sqrt(dx * dx + dy * dy);

        if (magnitude <= threshold)
        {
            x = 0;
            y = 0;
            return;
        }

        // Rescale the surviving range back to full travel so the stick still reaches its extremes.
        var scaled = (magnitude - threshold) / (32767.0 - threshold);
        var factor = scaled * 32767.0 / magnitude;

        x = Clamp(dx * factor);
        y = Clamp(dy * factor);
    }

    private static short Clamp(double v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);

    private static short Negate(short v) => v == short.MinValue ? short.MaxValue : (short)-v;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MappingProfile FromJson(string json) =>
        JsonSerializer.Deserialize<MappingProfile>(json, JsonOptions)
        ?? throw new InvalidDataException("Profile JSON did not contain an object.");

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, ToJson());
    }

    public static MappingProfile Load(string path) => FromJson(File.ReadAllText(path));
}
