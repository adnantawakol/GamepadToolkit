using GamepadToolkit.Core.Input;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GamepadToolkit.Core.Remap;

/// <summary>
/// Polls a physical pad, rewrites its state through a <see cref="MappingProfile"/>,
/// and republishes it on a ViGEm virtual Xbox 360 pad.
/// </summary>
public sealed class RemapEngine : IDisposable
{
    private static readonly (GamepadButton Source, Xbox360Button Target)[] ButtonMap =
    [
        (GamepadButton.A, Xbox360Button.A),
        (GamepadButton.B, Xbox360Button.B),
        (GamepadButton.X, Xbox360Button.X),
        (GamepadButton.Y, Xbox360Button.Y),
        (GamepadButton.LeftShoulder, Xbox360Button.LeftShoulder),
        (GamepadButton.RightShoulder, Xbox360Button.RightShoulder),
        (GamepadButton.Back, Xbox360Button.Back),
        (GamepadButton.Start, Xbox360Button.Start),
        (GamepadButton.Guide, Xbox360Button.Guide),
        (GamepadButton.LeftThumb, Xbox360Button.LeftThumb),
        (GamepadButton.RightThumb, Xbox360Button.RightThumb),
        (GamepadButton.DPadUp, Xbox360Button.Up),
        (GamepadButton.DPadDown, Xbox360Button.Down),
        (GamepadButton.DPadLeft, Xbox360Button.Left),
        (GamepadButton.DPadRight, Xbox360Button.Right)
    ];

    private ViGEmClient? _client;
    private IXbox360Controller? _virtualPad;
    private Thread? _worker;
    private volatile bool _running;

    private MappingProfile _profile = MappingProfile.Identity();

    public MappingProfile Profile
    {
        get => _profile;
        set => _profile = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool IsRunning => _running;
    public int? SourceIndex { get; private set; }

    /// <summary>The XInput slot our virtual pad occupies. Never usable as a source.</summary>
    public int? VirtualUserIndex { get; private set; }

    /// <summary>Raised on the worker thread with the post-remap state, for live UI feedback.</summary>
    public event Action<GamepadState>? StateChanged;

    public event Action<string>? Faulted;

    public static bool IsDriverInstalled()
    {
        try
        {
            using var probe = new ViGEmClient();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Start(int xinputIndex)
    {
        if (_running)
            return;

        _client = new ViGEmClient();
        _virtualPad = _client.CreateXbox360Controller();
        _virtualPad.AutoSubmitReport = false;

        // Record the slots in use before the virtual pad appears so we can tell which one it
        // claims. Feeding that slot back in as a source would have the pad read its own output.
        var occupiedBefore = XInputSource.OccupiedSlots();
        _virtualPad.Connect();
        Thread.Sleep(250);

        VirtualUserIndex = XInputSource.OccupiedSlots()
            .Except(occupiedBefore)
            .Select(s => (int?)s)
            .FirstOrDefault();

        SourceIndex = xinputIndex;
        _running = true;

        _worker = new Thread(() => Pump(xinputIndex))
        {
            IsBackground = true,
            Name = "GamepadToolkit.Remap",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _worker?.Join(500);
        _worker = null;

        try
        {
            _virtualPad?.Disconnect();
        }
        catch
        {
            // The bus may already have torn the target down; nothing to recover.
        }

        _virtualPad = null;
        _client?.Dispose();
        _client = null;
        SourceIndex = null;
        VirtualUserIndex = null;
    }

    private void Pump(int xinputIndex)
    {
        var source = new XInputSource(xinputIndex);
        var pad = _virtualPad;

        try
        {
            while (_running && pad is not null)
            {
                if (source.TryRead(out var physical))
                {
                    var remapped = _profile.Apply(physical);
                    Publish(pad, remapped);
                    StateChanged?.Invoke(remapped);
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Faulted?.Invoke(ex.Message);
        }
    }

    private static void Publish(IXbox360Controller pad, in GamepadState s)
    {
        foreach (var (source, target) in ButtonMap)
            pad.SetButtonState(target, s.IsPressed(source));

        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftStickX);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftStickY);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RightStickX);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RightStickY);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);

        pad.SubmitReport();
    }

    public void Dispose() => Stop();
}
