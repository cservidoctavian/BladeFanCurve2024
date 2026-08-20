# Blade Fan Curve

Temperature-driven fan control for Razer Blade laptops on Windows 10 and 11.

It talks to the laptop's embedded controller over the **same USB-HID channel Razer
Synapse uses** (vendor command class `0x0D`), but instead of Synapse's three fixed
performance modes it drives each fan continuously from a curve you draw against
live CPU and GPU temperatures.

Built for a **Razer Blade 14 (2024) — RZ09-0508, USB `1532:02B6`**, and tuned for
its Ryzen 9 8945HS and RTX 40-series GPU. Device discovery is model-agnostic, so
other Blades should work too.

<img width="1280" height="761" alt="image" src="https://github.com/user-attachments/assets/dc722f74-2c7a-4b23-ba5f-b219e7687797" />


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

## Keyboard lighting

The **Lighting** tab drives the Chroma backlight over the same HID channel as fan
control, so it needs no extra driver and no Synapse.

Effects come in two kinds, and the distinction matters:

**On the keyboard** — one command, then the keyboard's own controller runs the
effect. Costs nothing, survives the app closing, and keeps going after a reboot.

| Effect | Colours | Speed |
|---|---|---|
| Off | — | — |
| Static | 1 | — |
| Breathe | 1 | — |
| Breathe (two colours) | 2 | — |
| Breathe (random) | — | — |
| Spectrum | — | — |
| Wave | — | direction |
| Reactive — lights the key you press | 1 | 1–4 |
| Starlight | 1 | 1–3 |
| Starlight (two colours) | 2 | 1–3 |
| Starlight (random) | — | 1–3 |

**Rendered here** — the app draws each frame and streams it to the keyboard as a
custom frame. Anything is possible, at the cost of about seven HID writes per frame.

| Effect | Notes |
|---|---|
| Solid | Uniform colour |
| Gradient | Still fade, first colour to second |
| Breathe (smooth) | Smoother than the hardware version |
| Colour cycle | Whole board through the spectrum |
| Rainbow wave | Spectrum travelling across the keys |
| Scanner | Bright column sweeping with a trailing fade |
| Starfield | Keys twinkle at their own pace |
| Ripple | Rings spreading outward |
| Rain | Drops falling down the columns |
| Fire | Heat rising from the space bar |
| **Thermal** | Colour and fill follow the hottest of CPU and GPU |
| **Fan meter** | Top rows CPU fan, bottom rows GPU fan |

The last two are the reason lighting lives in this app rather than a separate one:
they read the same sensor and RPM data the fan curves run on, so the keyboard
becomes a gauge. Thermal runs blue at 45 °C through green and amber to red at
95 °C, and the number of lit columns tracks where in that range you are. Effects
marked with `·` in the list use live telemetry.

Frame rate defaults to 30 fps and is adjustable from 5 to 60. Lighting writes are
fire-and-forget and are dropped rather than queued whenever the control loop is
mid-command, so streaming frames can never delay a fan command.

The preview in the window is the actual frame that was sent to the keyboard, not a
mock-up — for hardware effects, where no frames pass through the app, it falls back
to a local approximation.

On exit the keyboard is left on a static colour rather than frozen on the last
rendered frame. Change that with `Lighting.RestoreOnExit` in the config
(`static`, `spectrum`, `off` or `leave`).

---

## Turning the fans off

Settings → **Let the fans stop completely (0 RPM)** drops the floor to zero, so the
curves and the manual slider can both go all the way down. The default floor is 2000
because that is where Razer's own manual range starts.

What makes that safe is the **thermal guard**, right below it: once *either* package
reaches **70 °C**, both fans are forced to at least **50%** of maximum, regardless of
what the curve or the manual override is asking for. Both numbers are editable.

Three tiers, in order of authority:

| | Trigger | Result |
|---|---|---|
| Curve or manual | — | whatever you asked for, down to 0 |
| Thermal guard | either package ≥ 70 °C | at least 50% of max |
| Critical | CPU ≥ 97 °C or GPU ≥ 88 °C | 100% |

The guard is a **floor, not a target**: a curve already asking for more than 50% is
not dragged down to it. It releases only after the machine has cooled by the release
margin (5 °C by default), so it cannot chatter on and off at the threshold.

Turning on the 0 RPM floor switches the guard on if it was off. Letting the fans stop
with no guard at all is the one combination the app will not set up silently.

**What the hardware does with 0 is its own business.** The value is sent as rpm/100
in a single byte, so 0 goes out as `0x00`, but whether the controller honours it or
clamps to its own minimum is firmware behaviour this app cannot override. The
measured RPM on the status tiles is the honest answer.

**One risk worth naming.** If the app is stopped at 0 RPM and *hangs* — not crashes,
which the failsafe catches — nothing here is running to spin the fans back up. The
sensor watchdog hands control back to the laptop after six seconds without a reading,
and the controller has its own thermal protection underneath all of this, but a 0 RPM
floor is a genuinely lower-margin setting than 2000. That is the trade for silence.

---

## On battery

Pull the charger out and the app switches to the **Silent** profile — which drops the
CPU and GPU power levels, moves Windows to the power-saver plan, and takes the panel
to 60 Hz. Plug back in and it returns to whatever was active before.

Both halves can be turned off separately, and the battery profile is a dropdown, so
"Silent" is a default rather than a rule.

Three details that make it behave rather than fight you:

**A profile you pick by hand wins.** If you unplug, get Silent, then deliberately
select Performance anyway, plugging the charger back in leaves you on Performance.
The app only ever undoes its own switch, never yours.

**Starting plugged in changes nothing.** The first power reading after launch
establishes a baseline; it does not "restore" a profile you never left. Starting *on*
battery does switch, because the machine really is on battery.

**Two agreeing reads are required.** Power status can report a transitional value for
a moment while the supply settles, and switching profiles writes config, changes the
Windows power plan and can change the refresh rate — far too much to do on a glitch.

The profile to return to is written to the config rather than held in memory, so
unplugging, closing the lid overnight and plugging in tomorrow still restores the
right one.

---

## Monitor — package power

A rolling 30-minute graph of CPU and GPU package power, sampled once a second.

Both series are watts, so they share **one y-axis**. Two scales on one plot would
make the lines look comparable when they are not — it is the most common way a chart
lies, and it is not worth the pixels it saves.

The history is owned by the control loop, not the window, so it keeps filling while
the app sits in the tray. Closing the window does not reset the graph.

**Gaps stay gaps.** A tick where nothing could be measured is stored as absent and
drawn as a break in the line. Joining across it would invent power draw that was
never measured, and a discrete GPU that has powered itself down would appear to be
sitting at 0 W rather than simply not reporting.

The series colours were checked rather than chosen by eye: CPU `#3AAD77` and GPU
`#6070DE` separate by ΔE 23.5 under deuteranopia and 26.5 for normal vision, both
well clear of the floors, and each clears 3:1 contrast against the card.

### Whether it will work on your machine

CPU package power and CPU package temperature both live behind model-specific
registers, which no user-mode API exposes. Reading them needs a kernel driver.

LibreHardwareMonitor 0.9.x uses **PawnIO** for this — a signed driver that runs
sandboxed bytecode modules rather than granting blanket ring-0 access. If it is not
installed, the CPU line cannot be drawn at all and the Source panel says so instead
of showing a flat line at zero. There is no ACPI or WMI fallback for wattage the way
there is for temperature.

**A correction worth reading.** Earlier versions of the library used WinRing0, which
Microsoft's vulnerable-driver blocklist and Memory Integrity genuinely do block — so
"turn off Core Isolation" is the advice all over the internet, and it is what this
project assumed at first. Version 0.9.6 does not ship or use WinRing0 at all.
Disabling Memory Integrity fixes nothing here and only makes the machine less safe.
The fix is to install PawnIO from [pawnio.eu](https://pawnio.eu/).

The app detects the driver, reports its version, and offers a link — it will not
install a kernel driver for you. That should be a deliberate decision made after
looking at the project yourself.

GPU board power comes from NVAPI, a user-mode library shipped with the display
driver, so it needs no kernel driver at all. On a laptop the discrete GPU often
reports nothing at idle because it has powered down; it appears under load.

---

## Power profiles

A profile is not just a pair of fan curves. Each one can also set the Razer
performance mode, CPU/GPU boost, the Windows power plan and power-mode slider, and
the display refresh rate — and switching profiles applies all of it at once.

| | Silent | Balanced | Performance |
|---|---|---|---|
| CPU power | Low | Medium | Boost |
| GPU power | Low | Medium | High |
| Performance mode | Custom | Custom | Custom |
| If Custom unavailable | Balanced (35 W) | Balanced (35 W) | Gaming (55 W) |
| Windows plan | Power saver | Balanced | High performance |
| Power mode | Best efficiency | Recommended | Best performance |
| Refresh rate | 60 Hz | leave | leave |

Selecting a profile on the **Fan curves** tab applies all of this at once — the
curves and the power settings are one switch, not two.

**Why every profile selects Custom.** The controller only honours CPU and GPU power
levels while it is in Custom mode. Setting a power level alongside Balanced or Gaming
does nothing at all — an earlier version made exactly that mistake, leaving Silent
and Balanced identical in power. If the firmware turns out not to expose the power
level commands, the profile drops to its named fallback mode instead, which still
moves the power target even though it cannot separate CPU from GPU.

Every field can be set to **Leave unchanged**, and that is the default for any
profile you create or that was written by an older version — upgrading never starts
silently moving your power plan.

**About "wattage".** Razer does not expose a watts figure. What it exposes is the
performance mode, and that *is* the power target: Balanced runs a 35 W CPU limit and
Gaming runs 55 W. Real PL1/PL2 control needs a ring-0 driver, the same thing Memory
Integrity blocks for CPU temperature, so it is not offered here.

Dropping to 60 Hz in the Silent profile is worth more battery than any of the rest
of this on a 240 Hz panel.

---

## Display

**Refresh rate** is changed through the standard Windows display API. The mode is
validated before it is applied, so an unsupported rate is refused rather than
blanking the screen.

**Blue light** is a scheduled warm filter driven through the display LUT, with the
warmth adjustable from 1200 K to 6500 K. Schedules that cross midnight work
correctly, which is the normal case and the one a naive comparison gets wrong.

This deliberately does not drive Windows' own Night Light. That feature is
controlled by an undocumented binary blob in the registry whose format changes
between builds; driving the gamma ramp is a documented API that does not break. The
trade-off is that the Windows toggle will not move.

**Colour profile** selects the ICC profile that colour-managed applications use.
Be clear about what this is not: Razer exposes no command for the panel's own gamut
mode, so this is the OS-level equivalent, not a hardware sRGB clamp.

**Response time / overdrive is not available.** It is a panel scaler setting with no
documented command, so rather than guess, it is absent.

The LUT is always reset on exit, including after a crash — a screen left tinted with
no obvious way to undo it would be worse than no feature at all.

---

## Battery care

Set a charge limit of 60, 80 or 100%. Above the limit the laptop runs from the
adapter and leaves the cell alone, which is the single biggest thing you can do for
its lifespan.

Razer exposes a threshold, not a separate "bypass" mode — setting 60 or 80% gets you
the practical outcome, but it is not a distinct AC-bypass feature and this app does
not claim to be one.

The threshold is re-sent after resume, because the controller forgets it across a
suspend.

### A note on how these two are implemented

The charge-limit and CPU/GPU-boost commands are not documented anywhere public that
could be verified. Guessing *write* bytes at an embedded controller is a different
risk class from guessing lighting bytes, so both are handled read-first:

1. Probe with the read-only `get` command. A read is non-mutating — unsupported
   firmware answers "not supported" and nothing happens.
2. Only if that read succeeds is the corresponding write offered at all.
3. Every write is confirmed by reading the value back, so a silent no-op is
   reported as a failure rather than shown as success.

If your firmware does not answer, the Power tab says so plainly and the controls are
disabled. Nothing is ever written into the dark.

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

### Lighting

Two command families exist and which one a keyboard speaks is not implied by its
product id, so it is probed once with a read-only *get brightness* — nothing visible
changes — and remembered.

| | Extended (class `0x0F`) | Standard (class `0x03`) |
|---|---|---|
| Effect | id `0x02`, args `[varstore, ledId, effectId, …]` | id `0x0A`, args `[effectId, …]` |
| Brightness | id `0x04`, size `0x03`, args `[varstore, ledId, level]` | id `0x03`, same args |
| Frame row | id `0x03`, size `0x47`, args `[0, 0, row, startCol, stopCol, rgb…]` | id `0x0B`, size `0x46`, args `[0xFF, row, startCol, stopCol, rgb…]` |

The 2024 Blades use the extended family with `varstore = 0x01` and
`ledId = 0x05` (backlight). Extended effect ids: `00` none, `01` static,
`02` breathing, `03` spectrum, `04` wave, `05` reactive, `07` starlight,
`08` custom frame.

The matrix is **6 rows x 16 columns**. One row of pixels is 48 bytes, which with a
5-byte header fits inside the report's 80 argument bytes with room to spare. A full
frame is six row writes plus a latch command telling the controller to display what
was uploaded.

All of these layouts are asserted byte-for-byte in the test suite against
OpenRazer's `razerchromacommon.c`, because a wrong layout fails silently — the
keyboard simply ignores the command.

### Discovery

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

The test suite is 321 checks over the report encoding, the CRC, curve
interpolation, the safety clamps, the model table, the HID access strategy and the
sensor fallback logic, plus guards on the build settings WPF cannot run under and on every XAML style
being applied to a compatible element type.
None of it needs Razer hardware attached.

### Layout

```
src/BladeFanCurve/
  Hardware/     RazerReport.cs, RazerLaptopDevice.cs,  HID transport and commands
                NativeHid.cs, KnownModels.cs,
                RazerChroma.cs, RazerPower.cs
  Lighting/     LightingEngine.cs,                     Chroma effects and the
                SoftwareEffects.cs                     per-key frame renderer
  Platform/     WindowsPowerPlan.cs,                   power plans, refresh rate,
                DisplayControl.cs,                     gamma and the blue-light
                NightLightService.cs                   schedule
  Sensors/      SensorService.cs                       LibreHardwareMonitor wrapper
  Control/      ControlLoop.cs, FanChannel.cs,         curve engine, watchdogs,
                FanCurveEvaluator.cs, Log.cs,          logging, logon task
                StartupTask.cs
  Config/       AppConfig.cs, ConfigStore.cs           JSON settings with clamping
  UI/           CurveEditor.cs, TrayManager.cs         curve editor, tray icon
  MainWindow.xaml(.cs), App.xaml(.cs)
tests/ProtocolTests/                                   321 checks, no hardware needed
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
