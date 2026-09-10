using GamepadToolkit.Core.Devices;
using HidSharp;

namespace GamepadToolkit.Core.Input;

/// <summary>
/// Works out which HID device sits in which XInput slot.
///
/// Windows exposes no API for this — XInput reports no VID/PID, and a HID path carries no user
/// index — so the link is inferred from behaviour: when a controller's HID report *content*
/// changes at the same moment as one slot's XInput packet number advances, that device and slot
/// are the same pad. A few coincidences are enough to be certain.
/// </summary>
public sealed class SlotCorrelator : IDisposable
{
    private const int CoincidenceWindowMs = 250;
    private const int RequiredScore = 4;

    private sealed class Watch
    {
        public required GamepadInfo Device { get; init; }
        public HidStream? Stream { get; set; }
        public Thread? Reader { get; set; }
        public volatile bool Running;
        public long LastChangeTicks;
        public byte[] LastReport = [];
    }

    private readonly Dictionary<string, Watch> _watches = [];
    private readonly Dictionary<(string Device, int Slot), int> _scores = [];
    private readonly Dictionary<string, int> _map = [];
    private readonly Dictionary<int, uint> _lastPackets = [];
    private readonly object _gate = new();

    /// <summary>Device instance id to XInput slot, for pads identified so far.</summary>
    public IReadOnlyDictionary<string, int> Map
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, int>(_map);
        }
    }

    public int? SlotFor(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return null;

        lock (_gate)
            return _map.TryGetValue(instanceId, out var slot) ? slot : null;
    }

    /// <summary>Begins watching any newly arrived physical pads and drops ones that vanished.</summary>
    public void Track(IReadOnlyList<GamepadInfo> devices)
    {
        var wanted = devices
            .Where(d => !d.IsVirtual && d.IsGameController && d.InstanceId is not null)
            .ToDictionary(d => d.InstanceId!);

        foreach (var gone in _watches.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            StopWatch(_watches[gone]);
            _watches.Remove(gone);
        }

        foreach (var (id, device) in wanted)
        {
            if (_watches.ContainsKey(id))
                continue;

            var watch = new Watch { Device = device };
            if (TryOpen(watch))
                _watches[id] = watch;
        }
    }

    private static bool TryOpen(Watch watch)
    {
        try
        {
            var hid = DeviceList.Local.GetHidDevices()
                .FirstOrDefault(d => string.Equals(d.DevicePath, watch.Device.DevicePath, StringComparison.OrdinalIgnoreCase));

            if (hid is null)
                return false;

            var stream = hid.Open();
            stream.ReadTimeout = 400;

            watch.Stream = stream;
            watch.Running = true;
            watch.Reader = new Thread(() => Pump(watch))
            {
                IsBackground = true,
                Name = $"GamepadToolkit.Correlate.{watch.Device.VidPid}"
            };
            watch.Reader.Start();
            return true;
        }
        catch
        {
            // Another process may hold the device exclusively; correlation just stays unknown.
            return false;
        }
    }

    private static void Pump(Watch watch)
    {
        var buffer = new byte[Math.Max(watch.Device.MaxInputReportLength, 64)];

        while (watch.Running && watch.Stream is not null)
        {
            int read;
            try
            {
                read = watch.Stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch
            {
                return;
            }

            if (read <= 0)
                continue;

            var report = buffer[..read];

            // Many pads stream reports continuously at rest, so arrival alone means nothing —
            // only a change in content counts as the user actually moving something.
            if (!report.AsSpan().SequenceEqual(watch.LastReport))
            {
                watch.LastReport = report;
                watch.LastChangeTicks = Environment.TickCount64;
            }
        }
    }

    /// <summary>Call regularly (a UI tick is ideal) to score coincidences.</summary>
    public void Sample()
    {
        var now = Environment.TickCount64;

        for (var slot = 0; slot < XInputSource.MaxUsers; slot++)
        {
            var packet = XInputSource.PacketNumber(slot);
            var known = _lastPackets.TryGetValue(slot, out var previous);
            _lastPackets[slot] = packet;

            if (!known || packet == 0 || packet == previous)
                continue;

            foreach (var watch in _watches.Values)
            {
                if (now - watch.LastChangeTicks > CoincidenceWindowMs)
                    continue;

                var key = (watch.Device.InstanceId!, slot);
                lock (_gate)
                    _scores[key] = _scores.GetValueOrDefault(key) + 1;
            }
        }

        Commit();
    }

    private void Commit()
    {
        lock (_gate)
        {
            foreach (var watch in _watches.Values)
            {
                var id = watch.Device.InstanceId!;
                if (_map.ContainsKey(id))
                    continue;

                var ranked = _scores
                    .Where(s => s.Key.Device == id)
                    .OrderByDescending(s => s.Value)
                    .ToList();

                if (ranked.Count == 0 || ranked[0].Value < RequiredScore)
                    continue;

                // Demand a clear winner so simultaneous input on two pads cannot mislabel them.
                var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;
                if (ranked[0].Value < runnerUp * 2)
                    continue;

                _map[id] = ranked[0].Key.Slot;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _scores.Clear();
            _map.Clear();
        }
    }

    private static void StopWatch(Watch watch)
    {
        watch.Running = false;

        try
        {
            watch.Stream?.Close();
        }
        catch
        {
            // Closing a device that already went away is not interesting.
        }

        watch.Stream = null;
    }

    public void Dispose()
    {
        foreach (var watch in _watches.Values)
            StopWatch(watch);

        _watches.Clear();
    }
}
