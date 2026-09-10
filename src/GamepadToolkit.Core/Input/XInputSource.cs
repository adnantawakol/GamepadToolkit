using System.Runtime.InteropServices;

namespace GamepadToolkit.Core.Input;

/// <summary>Reads a controller through XInput. Works for any XInput-capable pad, wired or Bluetooth.</summary>
public sealed class XInputSource
{
    public const int MaxUsers = 4;

    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;

    public int UserIndex { get; }
    public uint LastPacketNumber { get; private set; }

    public XInputSource(int userIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(userIndex, MaxUsers);
        ArgumentOutOfRangeException.ThrowIfNegative(userIndex);
        UserIndex = userIndex;
    }

    public bool TryRead(out GamepadState state)
    {
        state = default;

        if (!TryGetState((uint)UserIndex, out var raw))
            return false;

        LastPacketNumber = raw.dwPacketNumber;
        var g = raw.Gamepad;

        state.LeftStickX = g.sThumbLX;
        state.LeftStickY = g.sThumbLY;
        state.RightStickX = g.sThumbRX;
        state.RightStickY = g.sThumbRY;
        state.LeftTrigger = g.bLeftTrigger;
        state.RightTrigger = g.bRightTrigger;

        state.Set(GamepadButton.DPadUp, (g.wButtons & 0x0001) != 0);
        state.Set(GamepadButton.DPadDown, (g.wButtons & 0x0002) != 0);
        state.Set(GamepadButton.DPadLeft, (g.wButtons & 0x0004) != 0);
        state.Set(GamepadButton.DPadRight, (g.wButtons & 0x0008) != 0);
        state.Set(GamepadButton.Start, (g.wButtons & 0x0010) != 0);
        state.Set(GamepadButton.Back, (g.wButtons & 0x0020) != 0);
        state.Set(GamepadButton.LeftThumb, (g.wButtons & 0x0040) != 0);
        state.Set(GamepadButton.RightThumb, (g.wButtons & 0x0080) != 0);
        state.Set(GamepadButton.LeftShoulder, (g.wButtons & 0x0100) != 0);
        state.Set(GamepadButton.RightShoulder, (g.wButtons & 0x0200) != 0);
        state.Set(GamepadButton.Guide, (g.wButtons & 0x0400) != 0);
        state.Set(GamepadButton.A, (g.wButtons & 0x1000) != 0);
        state.Set(GamepadButton.B, (g.wButtons & 0x2000) != 0);
        state.Set(GamepadButton.X, (g.wButtons & 0x4000) != 0);
        state.Set(GamepadButton.Y, (g.wButtons & 0x8000) != 0);

        return true;
    }

    public static int? FindFirstConnected()
    {
        for (var i = 0u; i < MaxUsers; i++)
        {
            if (TryGetState(i, out _))
                return (int)i;
        }

        return null;
    }

    public static IReadOnlyList<int> OccupiedSlots()
    {
        var slots = new List<int>(MaxUsers);

        for (var i = 0u; i < MaxUsers; i++)
        {
            if (TryGetState(i, out _))
                slots.Add((int)i);
        }

        return slots;
    }

    /// <summary>Packet number only advances when the pad reports new data, so it detects activity.</summary>
    public static uint PacketNumber(int userIndex) =>
        TryGetState((uint)userIndex, out var state) ? state.dwPacketNumber : 0;

    private static bool TryGetState(uint index, out XInputState state)
    {
        try
        {
            return Native14.XInputGetState(index, out state) == ErrorSuccess;
        }
        catch (DllNotFoundException)
        {
            try
            {
                return Native13.XInputGetState(index, out state) == ErrorSuccess;
            }
            catch (DllNotFoundException)
            {
                state = default;
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    private static class Native14
    {
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        internal static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);
    }

    private static class Native13
    {
        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        internal static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);
    }
}
