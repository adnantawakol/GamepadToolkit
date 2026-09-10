using System.Diagnostics;
using System.Security.Principal;
using GamepadToolkit.Core.Devices;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace GamepadToolkit.Core.Remap;

/// <summary>
/// Wraps HidHide. Without it a remap produces doubled input, because games still see the
/// physical pad alongside the virtual one.
/// </summary>
public sealed class DeviceHider
{
    private readonly HidHideControlService? _service;

    public DeviceHider()
    {
        try
        {
            _service = new HidHideControlService();
        }
        catch
        {
            _service = null;
        }
    }

    public bool IsDriverInstalled
    {
        get
        {
            try
            {
                return _service?.IsInstalled == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool IsActive
    {
        get
        {
            try
            {
                return _service?.IsActive == true;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            if (_service is not null)
                _service.IsActive = value;
        }
    }

    public IReadOnlyList<string> BlockedDevices
    {
        get
        {
            try
            {
                return _service?.BlockedInstanceIds.ToList() ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>Whitelists this process so the toolkit keeps seeing the pad it is hiding from everything else.</summary>
    public void AllowThisApplication()
    {
        var path = Environment.ProcessPath;
        if (_service is null || string.IsNullOrEmpty(path))
            return;

        if (!_service.ApplicationPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _service.AddApplicationPath(path);
    }

    public void Hide(GamepadInfo device)
    {
        Require();

        if (device.IsVirtual)
        {
            throw new InvalidOperationException(
                $"{device.DisplayName} is the virtual pad that games are meant to see. Hiding it would " +
                "conceal the remapped output and leave the physical controller exposed — the exact opposite " +
                "of what you want. Select the physical controller instead.");
        }

        var instanceId = device.InstanceId
                         ?? throw new InvalidOperationException(
                             $"{device.DisplayName} has no resolvable PnP instance id, so it cannot be hidden.");

        AllowThisApplication();

        foreach (var node in ResolveChain(instanceId))
        {
            if (!_service!.BlockedInstanceIds.Contains(node, StringComparer.OrdinalIgnoreCase))
                _service.AddBlockedInstanceId(node);
        }

        _service!.IsActive = true;
    }

    public void Unhide(GamepadInfo device)
    {
        Require();

        if (device.InstanceId is not { } instanceId)
            return;

        foreach (var node in ResolveChain(instanceId))
            _service!.RemoveBlockedInstanceId(node);
    }

    /// <summary>
    /// Blocking only the leaf HID interface leaves the pad reachable through XInput, which reads
    /// the parent XUSB/BTHENUM node instead — that is how a hidden pad still shows up in browsers.
    /// Walk up the chain but stop before shared bus nodes, which are used by unrelated devices.
    /// </summary>
    public static IReadOnlyList<string> ResolveChain(string leafInstanceId)
    {
        var chain = new List<string> { leafInstanceId };
        var current = leafInstanceId;

        for (var depth = 0; depth < 4; depth++)
        {
            string? parent;
            try
            {
                parent = PnPDevice.GetDeviceByInstanceId(current)
                    .GetProperty<string>(DevicePropertyKey.Device_Parent);
            }
            catch
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(parent) || IsSharedBusNode(parent))
                break;

            chain.Add(parent);
            current = parent;
        }

        return chain;
    }

    private static bool IsSharedBusNode(string instanceId)
    {
        string[] busRoots = [@"BTH\", @"USB\ROOT_HUB", @"HTREE\", @"ROOT\", @"PCI\", @"ACPI\"];
        return busRoots.Any(r => instanceId.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    public void UnhideAll()
    {
        Require();
        _service!.ClearBlockedInstancesList();
        _service.IsActive = false;
    }

    /// <summary>Relaunches the toolkit elevated, which HidHide's control device requires.</summary>
    public static bool TryRelaunchElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch
        {
            // The user dismissed the UAC prompt.
            return false;
        }
    }

    private void Require()
    {
        if (_service is null || !IsDriverInstalled)
        {
            throw new InvalidOperationException(
                "HidHide is not installed. Install it with:  winget install Nefarius.HidHide");
        }

        if (!IsElevated)
        {
            throw new UnauthorizedAccessException(
                "Hiding a device requires administrator rights. Restart the toolkit as administrator.");
        }
    }
}
