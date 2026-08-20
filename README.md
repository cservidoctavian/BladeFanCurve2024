<img width="1277" height="760" alt="image" src="https://github.com/user-attachments/assets/baa0d89c-eaff-456a-be7d-efb98bafeadc" />


# Blade Fan Curve

Temperature-driven fan control for Razer Blade laptops on Windows 10 and 11.

It talks to the laptop's embedded controller over the **same USB-HID channel Razer
Synapse uses** (vendor command class `0x0D`), but instead of Synapse's three fixed
performance modes it drives each fan continuously from a curve you draw against
live CPU and GPU temperatures.

Built for a **Razer Blade 14 (2024) — RZ09-0508, USB `1532:02B6`**, and tuned for
its Ryzen 9 8945HS and RTX 40-series GPU. Device discovery is model-agnostic, so
other Blades should work too.

---

## What it does

- Reads CPU and GPU temperatures once a second via LibreHardwareMonitor.
- Maps each temperature through its own curve to an RPM set point.
- Writes the set points straight to the EC — no Synapse, no service, no telemetry.
- Falls back to Razer's own fan management the moment anything looks wrong.

Two independent curves: **fan 1 follows CPU temperature, fan 2 follows GPU
temperature**. If the discrete GPU is powered down (Optimus), the GPU fan follows
the CPU instead of idling at the curve floor.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11, 64-bit |
| Rights | Administrator (both the sensor driver and the HID control interface need it) |
| Runtime | .NET 8 Desktop Runtime — see below |
| CPU sensor | Falls back to ACPI thermal zones, no driver needed — see below |
| Synapse | Must not be running (see below) |

### The runtime

The shipped `BladeFanCurve.exe` is 5 MB and needs the .NET 8 Desktop Runtime.
One command, once:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

(or grab it from <https://dotnet.microsoft.com/download/dotnet/8.0> — you want
*.NET Desktop Runtime, x64*).

If you'd rather have a single file that depends on nothing, build the standalone
version instead — it bundles the runtime into one ~68 MB exe:

```powershell
.\build.ps1 -Standalone
```

That needs the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`) but only at
build time.

### Synapse

Synapse holds the control interface open and will keep re-applying its own
performance mode, so the two fight over the fans. Pick one:

- **Quit Synapse** from the tray before starting this app (simplest), or
- disable the *Razer Synapse Service* in `services.msc` if you don't use Synapse
  for anything else.

Chroma keyboard lighting is a separate interface and is not touched either way.

---

## First run

1. Run `BladeFanCurve.exe`. Windows will ask for elevation — that is expected.
2. The status dot goes green and the header shows
   `Razer Blade · 1532:02B6 · txn 0x1F` once the controller answers.
3. Open **Diagnostics → Run hardware self-test** and confirm you get real values
   for perf mode, RPM set point and measured RPM. Keep that output — it is the
   first thing to look at if anything misbehaves.
4. On the **Fan curves** tab, drag the points where you want them. Changes apply
   immediately and are saved as you go.
5. On **Settings**, press **Start with Windows** to register the elevated logon
   task, so it comes up on its own without a UAC prompt.

Closing the window leaves it running in the notification area. Quitting from the
tray menu hands the fans back to Razer on the way out.

---

## Where the temperatures come from

CPU and GPU temperature are read through separate paths, and only one of them is
guaranteed to work.

**GPU** comes from LibreHardwareMonitor via NVIDIA's user-mode NVAPI. No driver, no
problem.

**CPU** is harder. On AMD, package temperature lives in SMN registers that can only
be reached from ring 0, which needs a kernel driver. LibreHardwareMonitor does not
ship one in its NuGet package — it expects `WinRing0x64.sys` to be sitting next to
the executable — and that driver is on Microsoft's vulnerable-driver blocklist, so
Memory Integrity refuses to load it on a current Windows 11 install anyway.

So the CPU reading falls back through three layers:

| Layer | Source | Needs a driver |
|---|---|---|
| 1 | LibreHardwareMonitor package temperature | yes |
| 2 | ACPI thermal zone over WMI | no |
| 3 | The GPU reading, copied across | no |

Layer 2 is what you will normally be on. An ACPI thermal zone is a firmware-exposed
sensor near the CPU; it reads a few degrees cooler than the package and updates
more slowly, which is fine for driving a fan curve and is why the shipped curves
have gentle knees. Whichever layer is live is shown under **Settings → Sensors**,
and a warning strip appears above the tabs whenever the reading is degraded.

Layer 3 is the last resort — the CPU fan follows GPU temperature rather than
sitting at the curve floor with no idea how hot the machine is. If even that is
unavailable, the sensor watchdog hands the fans back to Razer.

If you want true package temperature, install HWiNFO or the LibreHardwareMonitor
desktop app once and let it place its driver, or copy `WinRing0x64.sys` next to
`BladeFanCurve.exe`. Layer 1 will then pick up automatically. It is not required.

---

## Editing curves

| Action | Result |
|---|---|
| Drag a point | Move it (a point can't cross its neighbours) |
| Double-click empty space | Add a point |
| Right-click a point | Remove it (minimum two points) |

Below the first point the first RPM is held; above the last point the last RPM is
held. Between points the value is linear. The dashed vertical line is the current
temperature and the white dot is where you actually are on the curve.

Three profiles ship by default — **Silent**, **Balanced**, **Performance** — and
you can add your own. Profiles are switchable from the tray menu.

---

## Safety

Manual fan control on a laptop is exactly as dangerous as the worst curve you can
draw, so there are several layers between a bad curve and a hot machine:

- **RPM floor.** No curve point can command below *Minimum RPM* (default 2000).
  The floor is enforced again at write time, not just in the editor.
- **Critical override.** At 97 °C CPU or 88 °C GPU both fans go to maximum
  immediately, ignoring the curve and the ramp limiter. It then holds for up to
  15 s while the temperature is still within 8 °C of the trigger, and releases
  early if the machine cools past that margin.
- **Sensor watchdog.** If no usable temperature arrives for 6 s, control is handed
  straight back to the laptop.
- **Device watchdog.** Five consecutive rejected commands trigger a reconnect, and
  the fans go back to automatic while that happens.
- **Crash recovery.** A marker file is written the moment manual mode is engaged
  and deleted when it is released. If the app finds that marker at startup, the
  previous run died in manual mode, so the fans are restored before anything else
  happens.
- **Every exit path restores automatic control** — normal quit, window close,
  unhandled exception, sleep, logoff and shutdown are all wired up.
- **Sleep/resume.** The EC forgets manual mode across suspend, so manual mode is
  re-established (and re-asserted every 20 s regardless) on resume.

The one thing the app cannot protect against is being force-killed from Task
Manager while the fans are pinned low. The marker file makes the next start clean
that up, and the RPM floor keeps the interim survivable, but don't make a habit of
it. Your CPU and GPU still have their own hardware thermal throttling underneath
all of this.

---

## Settings worth knowing

| Setting | Default | Notes |
|---|---|---|
| Minimum RPM | 2000 | The Blade 14 will not spin meaningfully below ~1800 |
| Maximum RPM | 5000 | Use **Diagnostics → Find max fan RPM** to get your real ceiling |
| CPU critical | 97 °C | Tuned for Ryzen (Tjmax 100). Drop to ~92 for an Intel H-series Blade |
| GPU critical | 88 °C | Just past where an RTX 40-series laptop GPU starts throttling |
| Poll interval | 1000 ms | Below ~500 ms you're mostly generating USB traffic |
| Temp fall rate | 1.5 °C/s | Lower = fan holds speed longer after a spike |
| Ramp up | 900 RPM/s | How fast the fan is allowed to speed up |
| Ramp down | 250 RPM/s | Deliberately slower — this is what stops audible hunting |
| Shared floor | off | On: neither fan drops below the hotter one's demand |
| Command delay | 30 ms | Raise it if diagnostics shows dropped replies |

Config lives at `%AppData%\BladeFanCurve\config.json` and can be hand-edited; it is
re-clamped on load. The log is beside it.

---

## Troubleshooting

**"No Razer laptop control interface found"**
Press **Diagnostics → Save full report**. It lists every HID interface on the
machine, the access mask Windows granted for each, and what each Razer interface
said when probed — which separates "the device is invisible", "it will not open"
and "it opens but does not speak command class 0x0D".

You can also generate the same report without the UI:

```powershell
.\BladeFanCurve.exe --diagnose
```

Note on how the device is opened: a Razer laptop's control interface is also its
system keyboard, and Windows refuses to give user mode a read/write handle to a
system keyboard. This app therefore opens the handle with a desired-access mask of
**zero**, which Windows does allow and which is still sufficient for
`HidD_SetFeature`/`HidD_GetFeature`. That is why it does not use an off-the-shelf
HID library — those ask for read/write and get ACCESS_DENIED.

**Fans don't respond even though the device is found**
Your firmware may use the other argument layout for the set-RPM command. The app
detects this automatically by reading the set point back and flipping if it
doesn't match — check the log for "switched set-rpm argument layout".

**RPM set point reads back lower than requested**
That's the EC clamping to its own ceiling. Press **Diagnostics → Find max fan
RPM** — it asks for an impossible speed and reports what the controller actually
accepted — then set *Maximum RPM* to the lower of the two zones. The fans go loud
for a couple of seconds while it runs.

**Fan speed oscillates**
Lower *Ramp down*, lower *Temp fall rate*, or flatten the steep section of the
curve. Oscillation is almost always a knee that's too steep.

---

## Protocol notes

Commands are 90-byte HID feature reports (91 on the wire, report id `0x00` first),
identical in layout to OpenRazer's `struct razer_report`:

```
[0]      status            0x00 request, 0x02 ok, 0x01 busy, 0x03 fail, 0x05 unsupported
[1]      transaction id    0x1F on Blade laptops
[2..3]   remaining packets (big endian)
[4]      protocol type
[5]      data size
[6]      command class     0x0D for laptop power/fan
[7]      command id
[8..87]  arguments
[88]     crc               XOR of bytes 2..87
[89]     reserved
```

| Operation | Class | Id | Size | Arguments |
|---|---|---|---|---|
| Set fan RPM | `0x0D` | `0x01` | `0x03` | `[v, zone, rpm/100]` |
| Get RPM set point | `0x0D` | `0x81` | `0x03` | `[0x00, zone]` → `args[2]×100` |
| Set perf mode | `0x0D` | `0x02` | `0x04` | `[0x00, zone, mode, manualFanFlag]` |
| Get perf mode | `0x0D` | `0x82` | `0x04` | `[0x00, zone]` → `args[2]`=mode, `args[3]`=flag |
| Read tachometer | `0x0D` | `0x88` | `0x04` | `[0x00, zone]` → `args[2]×100` |

Zones: `0x01` CPU fan, `0x02` GPU fan. Modes: `0` balanced, `1` gaming,
`2` creator, `4` custom. RPM travels as `rpm/100`, so everything is quantised to
100 RPM.

`v` differs by firmware generation: `0x01` on the 2024 Blades (`02B6`, `02B7`),
`0x00` on earlier ones. The shipped default comes from a small model table, and is
verified against the hardware at runtime by reading the set point back — if it
doesn't match, the app flips to the other layout and logs that it did.

The control interface is found by probing every Razer HID interface with a
read-only *get perf mode* command across the known transaction ids
(`1F 08 3F FF 00 88 9F`) and three reply delays, rather than by hard-coding product
ids, so this should work on other Blade models too. A *get firmware version*
(class `0x00`, id `0x81`) is used as a secondary probe so the report can tell
"this interface does not answer at all" apart from "it answers but has no fan
control".

HID access uses setupapi/hid.dll directly rather than a library, so the handle can
be opened with a zero desired-access mask — see the troubleshooting section.

---

## Building from source

```powershell
.\build.ps1              # ~5 MB exe, needs the .NET 8 Desktop Runtime
.\build.ps1 -Standalone  # ~68 MB exe, needs nothing
.\build.ps1 -Test        # run the test suite first
```

Or straight from the SDK:

```powershell
dotnet publish src\BladeFanCurve -c Release -o publish
dotnet run --project tests\ProtocolTests -c Release
```

The test suite is 90 checks over the report encoding, the CRC, curve
interpolation, the safety clamps, the model table, the HID access strategy and the
sensor fallback logic, plus a guard on the build settings WPF cannot run under.
None of it needs Razer hardware attached.

### Layout

```
src/BladeFanCurve/
  Hardware/     RazerReport.cs, RazerLaptopDevice.cs,  HID transport and commands
                NativeHid.cs, KnownModels.cs
  Sensors/      SensorService.cs                       LibreHardwareMonitor wrapper
  Control/      ControlLoop.cs, FanChannel.cs,         curve engine, watchdogs,
                FanCurveEvaluator.cs, Log.cs,          logging, logon task
                StartupTask.cs
  Config/       AppConfig.cs, ConfigStore.cs           JSON settings with clamping
  UI/           CurveEditor.cs, TrayManager.cs         curve editor, tray icon
  MainWindow.xaml(.cs), App.xaml(.cs)
tests/ProtocolTests/                                   51 checks, no hardware needed
install/                                               logon task scripts
```

### Dependencies

- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
  0.9.6 (MPL-2.0) — temperature sensors

HID access is P/Invoke against setupapi.dll and hid.dll, with no library in
between, because the zero-access open that this hardware requires is not something
general-purpose HID wrappers expose.

---

## Credit and disclaimer

The `0x0D` command class layout is public knowledge from the
[OpenRazer](https://github.com/openrazer/openrazer) project and its fan-control
pull request, [rnd-ash/razer-laptop-control](https://github.com/rnd-ash/razer-laptop-control),
and [TimandXiyu/razerblade-cli](https://github.com/TimandXiyu/razerblade-cli).

Not affiliated with or endorsed by Razer. Overriding your laptop's thermal
management is done at your own risk — start from the shipped curves and change
them gradually.
