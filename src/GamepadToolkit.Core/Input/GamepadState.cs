namespace GamepadToolkit.Core.Input;

public enum GamepadButton
{
    A, B, X, Y,
    LeftShoulder, RightShoulder,
    Back, Start, Guide,
    LeftThumb, RightThumb,
    DPadUp, DPadDown, DPadLeft, DPadRight
}

public enum GamepadAxis
{
    LeftStickX, LeftStickY,
    RightStickX, RightStickY,
    LeftTrigger, RightTrigger
}

public struct GamepadState
{
    public ushort ButtonMask;
    public short LeftStickX;
    public short LeftStickY;
    public short RightStickX;
    public short RightStickY;
    public byte LeftTrigger;
    public byte RightTrigger;

    public readonly bool IsPressed(GamepadButton b) => (ButtonMask & (1 << (int)b)) != 0;

    public void Set(GamepadButton b, bool pressed)
    {
        var bit = (ushort)(1 << (int)b);
        if (pressed) ButtonMask |= bit;
        else ButtonMask &= (ushort)~bit;
    }

    public readonly short GetAxis(GamepadAxis a) => a switch
    {
        GamepadAxis.LeftStickX => LeftStickX,
        GamepadAxis.LeftStickY => LeftStickY,
        GamepadAxis.RightStickX => RightStickX,
        GamepadAxis.RightStickY => RightStickY,
        GamepadAxis.LeftTrigger => LeftTrigger,
        GamepadAxis.RightTrigger => RightTrigger,
        _ => 0
    };

    public void SetAxis(GamepadAxis a, short value)
    {
        switch (a)
        {
            case GamepadAxis.LeftStickX: LeftStickX = value; break;
            case GamepadAxis.LeftStickY: LeftStickY = value; break;
            case GamepadAxis.RightStickX: RightStickX = value; break;
            case GamepadAxis.RightStickY: RightStickY = value; break;
            case GamepadAxis.LeftTrigger: LeftTrigger = ToTrigger(value); break;
            case GamepadAxis.RightTrigger: RightTrigger = ToTrigger(value); break;
        }
    }

    /// <summary>Sticks are signed 16-bit, triggers unsigned 8-bit; cross-assignment has to rescale.</summary>
    public static byte ToTrigger(short v) => v <= 0 ? (byte)0 : (byte)(v >> 7);

    public static short FromTrigger(byte v) => (short)(v << 7);

    public static bool IsStick(GamepadAxis a) =>
        a is not (GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger);
}
