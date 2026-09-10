# GamepadToolkit

A .NET 8 / WPF Windows app for inspecting, remapping, and (where the hardware
genuinely allows it) reflashing gamepad firmware.

## Why it's built the way it is

The original goal was to remap buttons by editing controller firmware
directly. That turns out to be impossible on most modern pads: Xbox,
PlayStation, and Nintendo controllers ship with signed firmware and
readout-protected MCUs, and a Bluetooth Classic link often exposes no vendor
command channel at all. Reflashing is only real for hardware that opens the
door itself — UF2 bootloader boards and QMK/VIA keyboards/pads.

So the app is split into three pieces:

- **Device inspector** — enumerates connected gamepads (XInput + raw HID) and
  identifies what they are.
- **Remap layer** — the part that actually delivers button remapping. It
  creates a virtual Xbox 360 controller with [ViGEmBus](https://github.com/ViGEm/ViGEmBus),
  reads the physical pad, applies a mapping profile, and forwards the result.
  Optionally hides the physical pad from other applications with
  [HidHide](https://github.com/nefarius/HidHide) so input isn't double-counted.
- **Firmware module** — attempts a real dump/flash only where the device
  permits it (UF2 volumes, VIA-compatible keyboards) and otherwise reports
  honestly why it can't, instead of pretending to succeed.

## Project layout

```
src/
  GamepadToolkit.Core/   Device enumeration, input capture, remap engine, firmware backends
  GamepadToolkit.App/    WPF UI (MainWindow, driver setup flow)
```

## Requirements

- Windows 10 1809+ or Windows 11, 64-bit
- .NET 8 SDK (to build)
- [ViGEmBus](https://github.com/ViGEm/ViGEmBus) driver — required for remapping
- [HidHide](https://github.com/nefarius/HidHide) driver — optional, hides the
  physical pad while remapped; without it every press registers on both the
  real and virtual controller

The app detects missing drivers on launch and offers to install them via
`winget`. Device inspection works without either driver.

## Building

```
dotnet build GamepadToolkit.sln -c Release
```

Framework-dependent publish:

```
dotnet publish src\GamepadToolkit.App\GamepadToolkit.App.csproj -c Release -o Installed
```

Portable single-file publish:

```
dotnet publish src\GamepadToolkit.App\GamepadToolkit.App.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -o Portable
```

## Hiding a controller

Hiding requires administrator rights and must happen *before* the game or
browser that shouldn't see the pad is started — HidHide blocks a device from
being opened, it can't revoke a handle a program already holds.

If the app is killed rather than closed normally, a hidden controller can
stay hidden. To undo that manually:

```
"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe" --cloak-off
```
