using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamepadToolkit.Core.Devices;
using GamepadToolkit.Core.Firmware;
using GamepadToolkit.Core.Firmware.Backends;
using GamepadToolkit.Core.Input;
using GamepadToolkit.Core.Remap;
using GamepadToolkit.Core.Setup;
using Microsoft.Win32;

namespace GamepadToolkit.App;

public partial class MainWindow : Window
{
    private readonly FirmwareService _firmware = new();
    private readonly RemapSession _session = new();
    private readonly DeviceHider _hider = new();
    private readonly DispatcherTimer _liveTimer;

    private readonly Dictionary<GamepadButton, Border> _buttonLights = [];
    private readonly Dictionary<GamepadAxis, (ProgressBar Bar, TextBlock Value)> _axisBars = [];
    private readonly Dictionary<GamepadButton, ComboBox> _mapCombos = [];

    private IReadOnlyList<GamepadInfo> _devices = [];
    private GamepadInfo? _selected;
    private MappingProfile _profile = MappingProfile.SwapXY();
    private bool _suppressUiEvents;
    private bool _autoSelectedSource;

    private readonly Dictionary<string, CheckBox> _hideBoxes = [];
    private readonly Dictionary<string, GamepadInfo> _hiddenDevices = [];
    private readonly SlotCorrelator _correlator = new();
    private string _hideAvailabilitySignature = string.Empty;

    private readonly Dictionary<int, (CheckBox Box, TextBlock Status, Border Dot)> _slotRows = [];
    private readonly Dictionary<int, GamepadState> _outputStates = [];
    private readonly Dictionary<int, uint> _lastPackets = [];

    public MainWindow()
    {
        InitializeComponent();

        BuildButtonLights();
        BuildAxisBars();
        BuildMapRows();

        BuildSourceSlots();

        _session.StateChanged += (slot, state) => _outputStates[slot] = state;
        _session.Faulted += (slot, message) => Dispatcher.Invoke(() =>
            SetStatus($"Remapping stopped for slot {slot + 1}: {message}", Severity.Bad));

        _liveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();

        LoadProfileIntoUi(_profile);

        // Must run before the first scan: it whitelists us with HidHide, without which a
        // device hidden by an earlier session stays invisible to this process too.
        UpdateDriverStatus();

        RefreshDevices();
        RefreshBootloaders();
    }

    // ---------- devices ----------

    private void RefreshDevices()
    {
        var previouslySelected = _selected?.InstanceId;

        _devices = DeviceScanner.Scan();
        DeviceList.Items.Clear();

        foreach (var device in _devices)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = device.DisplayName,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{device.VidPid}  ·  {Humanise(device.Transport)}",
                Style = (Style)FindResource("Label"),
                Margin = new Thickness(0, 3, 0, 0)
            });

            DeviceList.Items.Add(new ListBoxItem { Content = panel, Tag = device });
        }

        DeviceCountBadge.Text = _devices.Count == 1 ? "1 controller" : $"{_devices.Count} controllers";

        _correlator.Track(_devices);
        RebuildHideList();

        if (DeviceList.Items.Count > 0)
        {
            SelectPreferredDevice(previouslySelected);
            return;
        }

        // A sleeping Bluetooth pad still shows as "connected" in Windows Settings long after it
        // has dropped its HID interface, which reads as a driver fault but is not one.
        SetStatus("No game controllers detected. A Bluetooth pad can still show as connected in Windows " +
                  "after it has gone to sleep — press its Xbox/home button to wake it, then rescan.", Severity.Warn);
    }

    /// <summary>
    /// Keeps the user's selection across rescans and never lets the virtual pad inherit it.
    /// Once remapping starts, "Xbox 360 Controller" sorts ahead of "Xbox One S Controller",
    /// so a plain SelectedIndex = 0 would quietly retarget Hide at the remapped output.
    /// </summary>
    private void SelectPreferredDevice(string? previousInstanceId)
    {
        var items = DeviceList.Items.Cast<ListBoxItem>().ToList();

        var match = items.FirstOrDefault(i =>
                        ((GamepadInfo)i.Tag!).InstanceId is { } id &&
                        string.Equals(id, previousInstanceId, StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault(i => !((GamepadInfo)i.Tag!).IsVirtual)
                    ?? items[0];

        DeviceList.SelectedItem = match;
    }

    /// <summary>
    /// One checkbox per physical controller, named. The previous single checkbox acted on the
    /// Devices-tab selection, which is unrelated to what is being remapped — so it silently hid
    /// the wrong pad.
    /// </summary>
    private void RebuildHideList()
    {
        HideList.Children.Clear();
        _hideBoxes.Clear();

        var physical = _devices.Where(d => !d.IsVirtual && d.InstanceId is not null).ToList();

        if (physical.Count == 0)
        {
            HideList.Children.Add(new TextBlock
            {
                Text = "No physical controllers detected.",
                Style = (Style)FindResource("Label")
            });
            return;
        }

        foreach (var device in physical)
        {
            var box = new CheckBox
            {
                Content = device.DisplayName,
                Tag = device,
                IsChecked = _hiddenDevices.ContainsKey(device.InstanceId!),
                Margin = new Thickness(0, 0, 0, 3)
            };

            box.Checked += OnHideDeviceToggled;
            box.Unchecked += OnHideDeviceToggled;

            _hideBoxes[device.InstanceId!] = box;
            HideList.Children.Add(box);
        }

        // The boxes are new objects, so the cached signature no longer describes them.
        _hideAvailabilitySignature = string.Empty;
        UpdateHideAvailability();
    }

    private void OnHideDeviceToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        var box = (CheckBox)sender;
        var device = (GamepadInfo)box.Tag;
        var id = device.InstanceId!;

        try
        {
            if (box.IsChecked == true)
            {
                _hider.Hide(device);
                _hiddenDevices[id] = device;
                SetStatus($"{device.DisplayName} is hidden from other applications.", Severity.Good);
            }
            else
            {
                _hider.Unhide(device);
                _hiddenDevices.Remove(id);
                SetStatus($"{device.DisplayName} is visible to all applications again.");
            }
        }
        catch (Exception ex)
        {
            _suppressUiEvents = true;
            box.IsChecked = _hiddenDevices.ContainsKey(id);
            _suppressUiEvents = false;
            SetStatus(ex.Message, Severity.Bad);
        }
    }

    /// <summary>
    /// Hiding is only ever safe for a controller that is actually being remapped — hiding any
    /// other pad just makes it disappear with nothing standing in for it.
    /// </summary>
    private void UpdateHideAvailability()
    {
        var driverReady = _hider.IsDriverInstalled && DeviceHider.IsElevated;
        var remapping = _session.IsRunning;
        var active = _session.Sources.ToHashSet();

        var signature = string.Join('|',
            driverReady,
            remapping,
            string.Join(',', active.OrderBy(s => s)),
            string.Join(',', _hideBoxes.Keys.Select(k => $"{k}:{_correlator.SlotFor(k)}")));

        if (signature == _hideAvailabilitySignature)
            return;

        _hideAvailabilitySignature = signature;

        foreach (var (instanceId, box) in _hideBoxes)
        {
            var slot = _correlator.SlotFor(instanceId);
            var isRemapped = slot is { } s && active.Contains(s);

            box.IsEnabled = driverReady && remapping && isRemapped;

            box.ToolTip = !driverReady
                ? "Needs HidHide installed and the toolkit running as administrator."
                : !remapping
                    ? "Start remapping first. Hiding a controller that is not remapped would leave you with no working pad."
                    : slot is null
                        ? "Press a button on this controller so it can be matched to an XInput slot."
                        : !isRemapped
                            ? $"This controller is on slot {slot + 1}, which is not being remapped."
                            : null;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        RefreshBootloaders();
        UpdateDriverStatus();
        SetStatus("Device scan complete.");
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = (DeviceList.SelectedItem as ListBoxItem)?.Tag as GamepadInfo;
        if (_selected is null)
            return;

        RenderDetails(_selected);
        RenderFirmware(_selected);
        UpdateHideAvailability();
    }

    private void RenderDetails(GamepadInfo d)
    {
        DetailsPanel.Children.Clear();

        AddDetail("Product", d.ProductName ?? "(not reported)");
        AddDetail("Identified as", d.KnownName ?? "Not in known-device table");
        AddDetail("Vendor", $"{KnownDevices.VendorName(d.VendorId)}  ({d.VidPid})");
        AddDetail("Transport", Humanise(d.Transport) + (d.IsXInputCapable ? "  ·  XInput capable" : ""));
        AddDetail("HID usage", $"page 0x{d.UsagePage:X2}, usage 0x{d.Usage:X2}"
                               + (d.IsGameController ? "  (gamepad)" : d.HasViaInterface ? "  (VIA raw HID)" : ""));
        AddDetail("Report sizes", $"input {d.MaxInputReportLength} B  ·  output {d.MaxOutputReportLength} B  ·  feature {d.MaxFeatureReportLength} B");
        AddDetail("Report descriptor", $"{d.RawReportDescriptor.Length} bytes");
        AddDetail("Serial", d.SerialNumber ?? "(not reported)");
        AddDetail("Instance", d.InstanceId ?? d.DevicePath);
    }

    private void AddDetail(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var key = new TextBlock { Text = label, Style = (Style)FindResource("Label") };
        var val = new TextBlock { Text = value, Style = (Style)FindResource("Mono") };
        Grid.SetColumn(val, 1);

        grid.Children.Add(key);
        grid.Children.Add(val);
        DetailsPanel.Children.Add(grid);
    }

    // ---------- live input ----------

    private void BuildButtonLights()
    {
        foreach (var button in Enum.GetValues<GamepadButton>())
        {
            var light = new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("PanelAlt"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = Prettify(button.ToString()), FontSize = 12 }
            };

            _buttonLights[button] = light;
            ButtonLights.Children.Add(light);
        }
    }

    private void BuildAxisBars()
    {
        foreach (var axis in Enum.GetValues<GamepadAxis>())
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var label = new TextBlock { Text = Prettify(axis.ToString()), Style = (Style)FindResource("Label") };

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                Height = 8,
                Foreground = (Brush)FindResource("Accent"),
                Background = (Brush)FindResource("PanelAlt"),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(bar, 1);

            var value = new TextBlock { Text = "0", Style = (Style)FindResource("Mono") };
            Grid.SetColumn(value, 2);

            grid.Children.Add(label);
            grid.Children.Add(bar);
            grid.Children.Add(value);

            _axisBars[axis] = (bar, value);
            AxisBars.Children.Add(grid);
        }
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        GamepadState state;
        string hint;

        _correlator.Sample();
        UpdateSourceSlots();
        UpdateHideAvailability();

        if (_session.IsRunning)
        {
            var slot = _session.Sources.Min();
            state = _outputStates.TryGetValue(slot, out var output) ? output : default;
            hint = _session.Sources.Count == 1
                ? $"remapped output from slot {slot + 1}"
                : $"remapped output from slot {slot + 1}, +{_session.Sources.Count - 1} more";
        }
        else
        {
            var index = XInputSource.FindFirstConnected();
            if (index is null)
            {
                LiveHint.Text = "nothing reaching XInput — the pad may be asleep";
                return;
            }

            new XInputSource(index.Value).TryRead(out state);
            hint = $"raw input from XInput slot {index.Value + 1}";
        }

        LiveHint.Text = hint;

        var on = (Brush)FindResource("Accent");
        var off = (Brush)FindResource("PanelAlt");
        var border = (Brush)FindResource("Border");

        foreach (var (button, light) in _buttonLights)
        {
            var pressed = state.IsPressed(button);
            light.Background = pressed ? on : off;
            light.BorderBrush = pressed ? on : border;
        }

        foreach (var (axis, (bar, value)) in _axisBars)
        {
            var raw = state.GetAxis(axis);

            if (GamepadState.IsStick(axis))
            {
                bar.Value = (raw + 32768.0) / 65535.0 * 100.0;
                value.Text = raw.ToString();
            }
            else
            {
                var trigger = (byte)raw;
                bar.Value = trigger / 255.0 * 100.0;
                value.Text = trigger.ToString();
            }
        }
    }

    // ---------- remap ----------

    private void BuildSourceSlots()
    {
        for (var slot = 0; slot < XInputSource.MaxUsers; slot++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

            var box = new CheckBox
            {
                Content = $"Slot {slot + 1}",
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = slot
            };
            box.Checked += OnSourceSelectionChanged;
            box.Unchecked += OnSourceSelectionChanged;

            var status = new TextBlock
            {
                Text = "empty",
                Style = (Style)FindResource("Label"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(status, 1);

            var dot = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("PanelAlt"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dot, 2);

            grid.Children.Add(box);
            grid.Children.Add(status);
            grid.Children.Add(dot);

            _slotRows[slot] = (box, status, dot);
            SourceSlots.Children.Add(grid);
        }
    }

    private void UpdateSourceSlots()
    {
        var occupied = XInputSource.OccupiedSlots().ToHashSet();
        var virtualSlots = _session.VirtualSlots;
        var remapped = _session.Sources.ToHashSet();

        foreach (var (slot, row) in _slotRows)
        {
            var isOccupied = occupied.Contains(slot);
            var isVirtual = virtualSlots.Contains(slot);

            row.Status.Text = !isOccupied ? "empty"
                : isVirtual ? "virtual pad — remapped output"
                : remapped.Contains(slot) ? "being remapped"
                : "controller connected";

            row.Box.IsEnabled = isOccupied && !isVirtual && !_session.IsRunning;

            if (isVirtual && row.Box.IsChecked == true)
            {
                _suppressUiEvents = true;
                row.Box.IsChecked = false;
                _suppressUiEvents = false;
            }

            // The packet number only advances when the pad reports new data, so a change means input.
            var packet = isOccupied ? XInputSource.PacketNumber(slot) : 0u;
            var moved = _lastPackets.TryGetValue(slot, out var previous) && packet != previous;
            _lastPackets[slot] = packet;

            row.Dot.Background = moved
                ? (Brush)FindResource("Accent")
                : isOccupied
                    ? (Brush)FindResource("Border")
                    : (Brush)FindResource("PanelAlt");
        }

        // Tick the first real controller once, so the single-pad case needs no setup.
        if (_autoSelectedSource || _session.IsRunning)
            return;

        var firstReal = _slotRows.Keys
            .Where(s => occupied.Contains(s) && !virtualSlots.Contains(s))
            .OrderBy(s => s)
            .Cast<int?>()
            .FirstOrDefault();

        if (firstReal is not { } chosen)
            return;

        _suppressUiEvents = true;
        _slotRows[chosen].Box.IsChecked = true;
        _suppressUiEvents = false;
        _autoSelectedSource = true;
    }

    private List<int> SelectedSourceSlots() =>
        _slotRows.Where(r => r.Value.Box.IsChecked == true)
            .Select(r => r.Key)
            .OrderBy(s => s)
            .ToList();

    private void OnSourceSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        var chosen = SelectedSourceSlots();
        SetStatus(chosen.Count == 0
            ? "No controllers selected — tick at least one to remap."
            : $"Selected slot {string.Join(", ", chosen.Select(s => s + 1))} for remapping.");
    }

    private void BuildMapRows()
    {
        foreach (var source in Enum.GetValues<GamepadButton>())
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });

            var label = new TextBlock
            {
                Text = Prettify(source.ToString()),
                VerticalAlignment = VerticalAlignment.Center
            };

            var combo = new ComboBox
            {
                ItemsSource = Enum.GetValues<GamepadButton>().Select(b => Prettify(b.ToString())).ToArray(),
                SelectedIndex = (int)source,
                Tag = source
            };
            combo.SelectionChanged += OnMappingChanged;
            Grid.SetColumn(combo, 1);

            grid.Children.Add(label);
            grid.Children.Add(combo);

            _mapCombos[source] = combo;
            MapRows.Children.Add(grid);
        }
    }

    private void OnMappingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        CollectProfileFromUi();
        SetStatus("Mapping updated." + (_session.IsRunning ? " Applied live." : ""));
    }

    private void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (!_suppressUiEvents)
            CollectProfileFromUi();
    }

    private void OnDeadzoneChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LeftDeadzoneLabel is null || RightDeadzoneLabel is null)
            return;

        LeftDeadzoneLabel.Text = $"Left deadzone: {(int)LeftDeadzone.Value}";
        RightDeadzoneLabel.Text = $"Right deadzone: {(int)RightDeadzone.Value}";

        if (!_suppressUiEvents)
            CollectProfileFromUi();
    }

    private void CollectProfileFromUi()
    {
        _profile.Name = string.IsNullOrWhiteSpace(ProfileName.Text) ? "Untitled" : ProfileName.Text.Trim();
        _profile.Description = string.IsNullOrWhiteSpace(ProfileDescription.Text) ? null : ProfileDescription.Text.Trim();
        _profile.TargetVidPid = _selected?.VidPid;
        _profile.TargetDeviceName = _selected?.DisplayName;
        _profile.LeftStickDeadzone = (int)LeftDeadzone.Value;
        _profile.RightStickDeadzone = (int)RightDeadzone.Value;
        _profile.InvertLeftStickY = InvertLeftY.IsChecked == true;
        _profile.InvertRightStickY = InvertRightY.IsChecked == true;

        _profile.Buttons.Clear();
        foreach (var (source, combo) in _mapCombos)
        {
            var target = (GamepadButton)combo.SelectedIndex;
            if (target != source)
                _profile.Buttons[source] = target;
        }

        _session.Profile = _profile;
    }

    private void LoadProfileIntoUi(MappingProfile profile)
    {
        _suppressUiEvents = true;

        _profile = profile;
        ProfileName.Text = profile.Name;
        ProfileDescription.Text = profile.Description ?? string.Empty;
        LeftDeadzone.Value = profile.LeftStickDeadzone;
        RightDeadzone.Value = profile.RightStickDeadzone;
        InvertLeftY.IsChecked = profile.InvertLeftStickY;
        InvertRightY.IsChecked = profile.InvertRightStickY;

        foreach (var (source, combo) in _mapCombos)
        {
            var target = profile.Buttons.TryGetValue(source, out var mapped) ? mapped : source;
            combo.SelectedIndex = (int)target;
        }

        LeftDeadzoneLabel.Text = $"Left deadzone: {profile.LeftStickDeadzone}";
        RightDeadzoneLabel.Text = $"Right deadzone: {profile.RightStickDeadzone}";

        _suppressUiEvents = false;
        _session.Profile = _profile;
    }

    private void OnPresetSwapXyClick(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoUi(MappingProfile.SwapXY());
        SetStatus("Loaded the X / Y swap preset.");
    }

    private void OnPresetSwapAbClick(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoUi(MappingProfile.SwapAB());
        SetStatus("Loaded the A / B swap preset.");
    }

    private void OnPresetSwapBothClick(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoUi(MappingProfile.SwapFaceButtons());
        SetStatus("Loaded the combined X/Y and A/B swap preset.");
    }

    private void OnResetMappingClick(object sender, RoutedEventArgs e)
    {
        LoadProfileIntoUi(MappingProfile.Identity());
        SetStatus("Mapping reset to passthrough.");
    }

    private void UpdateDriverStatus()
    {
        if (RemapEngine.IsDriverInstalled())
        {
            DriverStatus.Text = "ViGEmBus detected — the virtual controller can be created.";
            DriverStatus.Foreground = (Brush)FindResource("Good");
            StartStopButton.IsEnabled = true;
        }
        else
        {
            DriverStatus.Text =
                "ViGEmBus is not installed. It provides the virtual controller that games read instead of your physical pad.";
            DriverStatus.Foreground = (Brush)FindResource("Warn");
            StartStopButton.IsEnabled = false;
        }

        if (!_hider.IsDriverInstalled)
        {
            HidHideStatus.Text =
                "HidHide is not installed. Without it games see the physical pad as well as the virtual one, " +
                "so every input registers twice.";
            HidHideStatus.Foreground = (Brush)FindResource("Warn");
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else if (!DeviceHider.IsElevated)
        {
            HidHideStatus.Text = "HidHide detected, but hiding a device needs administrator rights.";
            HidHideStatus.Foreground = (Brush)FindResource("Warn");
            ElevateButton.Visibility = Visibility.Visible;
        }
        else
        {
            // Self-register before anything else. A device hidden by an earlier run would
            // otherwise be invisible to this build too, leaving no selectable device and so
            // no way to reach the checkbox that would have whitelisted it.
            try
            {
                _hider.AllowThisApplication();
            }
            catch
            {
                // Not fatal — hiding still works, the toggle just has to register us instead.
            }

            HidHideStatus.Text = "HidHide detected — controllers can be hidden from games.";
            HidHideStatus.Foreground = (Brush)FindResource("Good");
            ElevateButton.Visibility = Visibility.Collapsed;
        }

        // Always reachable, not just when something is missing: it is also where the download
        // links and the reboot warning live, and a driver can disappear mid-session.
        InstallDriversButton.Content = DriverBootstrap.Missing().Count > 0
            ? "Install missing drivers…"
            : "Driver setup…";

        UpdateHideAvailability();
    }

    private void OnInstallDriversClick(object sender, RoutedEventArgs e)
    {
        var window = new DriverSetupWindow { Owner = this };
        window.ShowDialog();

        UpdateDriverStatus();

        // A newly installed HidHide can now hide devices an earlier scan never saw.
        RefreshDevices();

        SetStatus(window.RebootRecommended
            ? "Driver setup finished. Restart Windows before hiding a controller."
            : "Driver setup finished.");
    }

    private void RestoreHiddenDevices()
    {
        foreach (var device in _hiddenDevices.Values)
        {
            try
            {
                _hider.Unhide(device);
            }
            catch
            {
                // Nothing useful to do during teardown; HidHide's own UI can clear a stale entry.
            }
        }

        _hiddenDevices.Clear();
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        if (DeviceHider.TryRelaunchElevated())
            Application.Current.Shutdown();
        else
            SetStatus("Elevation was declined, so device hiding stays unavailable.", Severity.Warn);
    }

    private void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning)
        {
            _session.StopAll();
            _outputStates.Clear();

            // A hidden pad with nothing standing in for it is just a dead controller.
            var restored = _hiddenDevices.Count;
            RestoreHiddenDevices();

            foreach (var box in _hideBoxes.Values)
            {
                _suppressUiEvents = true;
                box.IsChecked = false;
                _suppressUiEvents = false;
            }

            StartStopButton.Content = "Start remapping";
            SetStatus(restored > 0
                ? $"Remapping stopped, and {restored} hidden controller(s) are visible again."
                : "Remapping stopped.");
            return;
        }

        var chosen = SelectedSourceSlots();
        if (chosen.Count == 0)
        {
            SetStatus("Tick at least one controller under \"Controllers to remap\" first.", Severity.Warn);
            return;
        }

        try
        {
            CollectProfileFromUi();
            _session.Profile = _profile;
            _session.Start(chosen);

            StartStopButton.Content = "Stop remapping";
            var slots = string.Join(", ", chosen.Select(s => s + 1));
            SetStatus($"Remapping slot {slots} through a virtual pad each. " +
                      "Games see both the real and virtual pads until you hide the real ones.", Severity.Good);
        }
        catch (Exception ex)
        {
            _session.StopAll();
            SetStatus($"Could not start remapping: {ex.Message}", Severity.Bad);
        }
    }

    private void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        CollectProfileFromUi();

        var dialog = new SaveFileDialog
        {
            Title = "Export mapping profile",
            Filter = "Gamepad profile (*.json)|*.json",
            FileName = Sanitise(_profile.Name) + ".json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _profile.Save(dialog.FileName);
            SetStatus($"Profile exported to {dialog.FileName}", Severity.Good);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", Severity.Bad);
        }
    }

    private void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import mapping profile",
            Filter = "Gamepad profile (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            LoadProfileIntoUi(MappingProfile.Load(dialog.FileName));
            SetStatus($"Imported \"{_profile.Name}\".", Severity.Good);
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}", Severity.Bad);
        }
    }

    // ---------- firmware ----------

    private void RenderFirmware(GamepadInfo device)
    {
        var result = _firmware.Probe(device);

        FirmwareSummary.Text = $"{device.DisplayName} — {result.Summary}";
        FirmwareDetail.Text = result.Detail ?? string.Empty;

        AccessBadgeText.Text = result.Access.ToString();
        var brush = result.Access switch
        {
            FirmwareAccess.ReadWrite or FirmwareAccess.ConfigOnly => (Brush)FindResource("Good"),
            FirmwareAccess.WriteOnly or FirmwareAccess.VendorToolOnly => (Brush)FindResource("Warn"),
            FirmwareAccess.Locked or FirmwareAccess.None => (Brush)FindResource("Bad"),
            _ => (Brush)FindResource("Muted")
        };
        AccessBadgeText.Foreground = brush;

        BackupFirmwareButton.IsEnabled = result.CanRead;
        RestoreFirmwareButton.IsEnabled = result.CanWrite;
    }

    private async void OnBackupFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save firmware backup",
            Filter = "Firmware image (*.bin)|*.bin",
            FileName = $"{Sanitise(_selected.DisplayName)}-{DateTime.Now:yyyyMMdd-HHmmss}.bin"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SetStatus("Reading firmware…");
            var image = await _firmware.ReadAsync(_selected);
            var manifest = image.Save(dialog.FileName);
            SetStatus($"Backed up {image.Length:N0} bytes. SHA-256 recorded in {Path.GetFileName(manifest)}.", Severity.Good);
        }
        catch (Exception ex)
        {
            ShowRefusal("Backup not possible", ex);
        }
    }

    private async void OnRestoreFirmwareClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose firmware image to restore",
            Filter = "Firmware image (*.bin)|*.bin|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var confirm = MessageBox.Show(this,
            $"Write this image to {_selected.DisplayName}?\n\nA failed write can leave the device unusable. " +
            "Make sure you have a verified backup first.",
            "Confirm firmware write", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var image = FirmwareImage.Load(dialog.FileName);
            SetStatus("Writing firmware…");
            await _firmware.WriteAsync(_selected, image);
            SetStatus($"Wrote {image.Length:N0} bytes to {_selected.DisplayName}.", Severity.Good);
        }
        catch (Exception ex)
        {
            ShowRefusal("Restore not possible", ex);
        }
    }

    private void RefreshBootloaders()
    {
        BootloaderPanel.Children.Clear();
        var volumes = _firmware.DiscoverBootloaders();

        if (volumes.Count == 0)
        {
            BootloaderPanel.Children.Add(new TextBlock
            {
                Text = "No UF2 bootloader volumes found.",
                Style = (Style)FindResource("Label")
            });
            return;
        }

        foreach (var volume in volumes)
            BootloaderPanel.Children.Add(BuildBootloaderCard(volume));
    }

    private Border BuildBootloaderCard(Uf2Volume volume)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = volume.DisplayName, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = $"{volume.Root}  ·  board {volume.BoardId ?? "unknown"}  ·  {volume.FreeBytes / 1024:N0} KB free" +
                   (volume.HasReadableImage ? "  ·  CURRENT.UF2 present" : "  ·  write-only bootloader"),
            Style = (Style)FindResource("Label"),
            Margin = new Thickness(0, 4, 0, 10)
        });

        var buttons = new WrapPanel();

        var dump = new Button { Content = "Dump image", IsEnabled = volume.HasReadableImage };
        dump.Click += (_, _) => DumpBootloader(volume);

        var flash = new Button { Content = "Flash image", Margin = new Thickness(0) };
        flash.Click += (_, _) => FlashBootloader(volume);

        buttons.Children.Add(dump);
        buttons.Children.Add(flash);
        stack.Children.Add(buttons);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private void DumpBootloader(Uf2Volume volume)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save UF2 dump",
            Filter = "UF2 image (*.uf2)|*.uf2",
            FileName = $"{Sanitise(volume.BoardId ?? "board")}-{DateTime.Now:yyyyMMdd-HHmmss}.uf2"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var image = volume.Read();
            image.Save(dialog.FileName);
            SetStatus($"Dumped {image.Length:N0} bytes from {volume.DisplayName}.", Severity.Good);
        }
        catch (Exception ex)
        {
            ShowRefusal("Dump not possible", ex);
        }
    }

    private void FlashBootloader(Uf2Volume volume)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose UF2 image to flash",
            Filter = "UF2 image (*.uf2)|*.uf2"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var confirm = MessageBox.Show(this,
            $"Flash this image to {volume.DisplayName}?\n\nThe image is validated block by block before any bytes are written, " +
            "but flashing still replaces the board's firmware.",
            "Confirm flash", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var image = FirmwareImage.Load(dialog.FileName);
            volume.Flash(image);
            SetStatus($"Flashed {image.Length:N0} bytes to {volume.DisplayName}. The board will reboot.", Severity.Good);
            RefreshBootloaders();
        }
        catch (Exception ex)
        {
            ShowRefusal("Flash refused", ex);
        }
    }

    private void OnRescanBootloadersClick(object sender, RoutedEventArgs e)
    {
        RefreshBootloaders();
        SetStatus("Bootloader scan complete.");
    }

    // ---------- shared ----------

    private void ShowRefusal(string title, Exception ex)
    {
        var message = ex is AggregateException a ? a.InnerException?.Message ?? a.Message : ex.Message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        SetStatus(message, Severity.Warn);
    }

    private enum Severity { Normal, Good, Warn, Bad }

    private void SetStatus(string message, Severity severity = Severity.Normal)
    {
        StatusText.Text = message;
        StatusText.Foreground = severity switch
        {
            Severity.Good => (Brush)FindResource("Good"),
            Severity.Warn => (Brush)FindResource("Warn"),
            Severity.Bad => (Brush)FindResource("Bad"),
            _ => (Brush)FindResource("Muted")
        };
    }

    private static string Humanise(DeviceTransport t) => t switch
    {
        DeviceTransport.Usb => "USB",
        DeviceTransport.BluetoothClassic => "Bluetooth Classic",
        DeviceTransport.BluetoothLe => "Bluetooth LE",
        _ => "Unknown transport"
    };

    /// <summary>"LeftShoulder" -> "Left shoulder", "DPadUp" -> "D-pad up".</summary>
    private static string Prettify(string pascalCase)
    {
        if (pascalCase.StartsWith("DPad", StringComparison.Ordinal))
            return "D-pad " + pascalCase[4..].ToLowerInvariant();

        var sb = new StringBuilder(pascalCase.Length + 4);

        for (var i = 0; i < pascalCase.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascalCase[i]) && !char.IsUpper(pascalCase[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(pascalCase[i]));
            }
            else
            {
                sb.Append(pascalCase[i]);
            }
        }

        return sb.ToString();
    }

    private static string Sanitise(string name) =>
        string.Join('-', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

    protected override void OnClosed(EventArgs e)
    {
        _liveTimer.Stop();
        _session.Dispose();
        _correlator.Dispose();

        // Leaving a device hidden after exit would make it look broken everywhere else.
        RestoreHiddenDevices();

        base.OnClosed(e);
    }
}
