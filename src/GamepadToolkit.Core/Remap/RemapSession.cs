using GamepadToolkit.Core.Input;

namespace GamepadToolkit.Core.Remap;

/// <summary>
/// Runs one <see cref="RemapEngine"/> per chosen source slot, each with its own virtual pad,
/// so several controllers can be remapped at once under a shared profile.
/// </summary>
public sealed class RemapSession : IDisposable
{
    private readonly Dictionary<int, RemapEngine> _engines = [];
    private MappingProfile _profile = MappingProfile.Identity();

    public MappingProfile Profile
    {
        get => _profile;
        set
        {
            _profile = value ?? throw new ArgumentNullException(nameof(value));

            foreach (var engine in _engines.Values)
                engine.Profile = _profile;
        }
    }

    public bool IsRunning => _engines.Count > 0;

    public IReadOnlyCollection<int> Sources => _engines.Keys;

    /// <summary>Slots held by virtual pads this session created. Never valid as sources.</summary>
    public IReadOnlySet<int> VirtualSlots =>
        _engines.Values
            .Where(e => e.VirtualUserIndex is not null)
            .Select(e => e.VirtualUserIndex!.Value)
            .ToHashSet();

    public event Action<int, GamepadState>? StateChanged;
    public event Action<int, string>? Faulted;

    public void Start(IReadOnlyCollection<int> sourceSlots)
    {
        if (sourceSlots.Count == 0)
            throw new InvalidOperationException("Select at least one controller to remap.");

        // Every remapped pad adds a virtual one, and XInput only ever exposes four slots.
        var free = XInputSource.MaxUsers - XInputSource.OccupiedSlots().Count;
        var wanted = sourceSlots.Count(s => !_engines.ContainsKey(s));

        if (wanted > free)
        {
            throw new InvalidOperationException(
                $"Remapping {wanted} controller(s) needs {wanted} more XInput slots but only {free} are free. " +
                "XInput supports four in total, and each remapped pad consumes one for its virtual counterpart. " +
                "Disconnect a controller or remap fewer of them.");
        }

        foreach (var slot in sourceSlots.Distinct().OrderBy(s => s))
        {
            if (_engines.ContainsKey(slot))
                continue;

            if (VirtualSlots.Contains(slot))
            {
                throw new InvalidOperationException(
                    $"XInput slot {slot + 1} is a virtual pad created by this session. Remapping it would " +
                    "feed its own output back into itself.");
            }

            var engine = new RemapEngine { Profile = _profile };
            engine.StateChanged += state => StateChanged?.Invoke(slot, state);
            engine.Faulted += message => Faulted?.Invoke(slot, message);

            engine.Start(slot);
            _engines[slot] = engine;
        }
    }

    public void StopAll()
    {
        foreach (var engine in _engines.Values)
            engine.Dispose();

        _engines.Clear();
    }

    public void Dispose() => StopAll();
}
