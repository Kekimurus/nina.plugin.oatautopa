# OAT AutoPA – N.I.N.A. plugin

[![Build](https://github.com/Kekimurus/nina.plugin.oatautopa/actions/workflows/build.yml/badge.svg)](https://github.com/Kekimurus/nina.plugin.oatautopa/actions/workflows/build.yml)

Automatic polar alignment for the **OpenAstroTracker / OpenAstroMount** AZ/ALT motors, driven by
**Three Point Polar Alignment (TPPA)**.

TPPA measures the polar error. This plugin moves the AZ/ALT axes through the mount N.I.N.A. is already
connected to. While the motors turn it holds TPPA's capture, so no frame is taken of a moving field. That
fixes the delay of the OATControl log-reader approach, which lets TPPA keep exposing during moves.

## How it works

It uses TPPA's **external correction bridge** (TPPA 2.2.8.0+, see
[`docs/external-correction.md`](https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/docs/external-correction.md)):

1. The plugin announces itself on N.I.N.A.'s message broker, and TPPA lists it as **External: OAT AutoPA**.
2. After the three reference frames, TPPA publishes each polar error measurement and waits.
3. For every measurement the plugin:
   1. asks TPPA for a capture window (`BeginAdjustment`);
   2. sends `:MAZ`/`:MAL`;
   3. polls `:GX` until both motors stop, sending keep-alives from a separate timer the whole time;
   4. asks for the next measurement with a settle time.
4. Within tolerance it stops moving. When TPPA's finish condition is met, it asks TPPA to complete.

The plugin talks to the firmware only through the ASCOM driver's `CommandBlind`/`CommandString`
passthrough. No second serial connection is opened.

Before it offers the hardware to TPPA, the plugin asks the firmware for its product name (`:GVP#`) through
that same passthrough. Only `OpenAstroTracker` or `OpenAstroMount` counts as connected. Any other mount,
or a driver that rejects the command, is reported to TPPA as not connected, with the reason, so the external
step never starts on the wrong mount. The check runs at every session start and again after N.I.N.A.
reconnects the mount. The firmware version (`:GVN#`) and the driver name are logged.

### Calibration from the sky, during the run

There are no dedicated calibration frames. Each correction doubles as a calibration sample:

- **Gear response:** the measured error change divided by the commanded arcminutes, on moves that keep the
  direction. Once two or more samples agree, moves are scaled by it (it effectively corrects the firmware's
  steps/degree).
- **Backlash**, handled by one of two strategies (option *Backlash strategy*):
  - **Preload** (default): every negative move overshoots by the preload and comes back positive, so the
    gears always finish turning the same way. No backlash value needs to be known, only a preload at
    least as large as the real play (default 5′ per axis). It is the same idea as TPPA's own
    `BacklashCompensationPlanner`, but the overshoot is folded into the move, so it takes two motor
    commands instead of three. Each correction then behaves like a same-direction move, which also makes
    every move a clean response sample.
  - **Learned**: backlash is measured after each direction reversal, as the travel that went missing
    compared with the response. Once trusted, it is added to every reversal. No extra motion, but it
    needs a trusted response first and real reversals to learn from.
- **Slippage guard:** if the response samples scatter more than the configured limit (default 25 %), the
  axis is marked *unreliable*. The plugin then falls back to plain, uncompensated moves instead of applying
  numbers it can't trust.
- **Direction detection:** if a move clearly makes the error worse before anything is trusted, the axis is
  flipped, and the flip is saved to the options.
- Trusted numbers are stored per N.I.N.A. profile and used as a starting point for the next session.

Moves smaller than 1.5′ (configurable) are not used for learning, because measurement noise would dominate.

## Requirements

- N.I.N.A. 3.1.2.9001 or later.
- Three Point Polar Alignment 2.2.8.0 or later.
- OAT/OAM firmware with motorised AZ/ALT. It must support `:MAZ`/`:MAL` and report AZ/ALT motion in `:GX`.
- The mount connected in N.I.N.A. through the OpenAstroTracker ASCOM driver.

## Using it

1. Install the plugin and restart N.I.N.A.
2. In TPPA's options, set **Polar Alignment System** to **External: OAT AutoPA**, set a **non-zero
   alignment tolerance**.
3. Run TPPA as a **sequence instruction**. The bridge only runs there, not in the dockable.
4. Get within the *manual adjustment* limit (default 120′) by hand first. The plugin refuses larger errors.
5. If using OATracker alongside N.I.N.A., make sure to disable its AutoPA functionality.

The first time, watch the first two corrections. If an axis goes the wrong way, direction detection
flips it. You can also set the invert options yourself.

## Options

| Option | Default | Notes |
|---|---|---|
| Correction gain | 0.8 | Fraction of the error corrected per step. |
| Maximum move per step | 30′ | Bigger corrections are split over several steps. |
| Manual adjustment above | 120′ | The run stops with a message above this. |
| Settle time | 1.5 s | TPPA waits this long after a move before exposing. |
| Invert azimuth / altitude | off | Altitude is also flipped automatically in the southern hemisphere. |
| Backlash strategy | Preload | *Preload* or *Learned*, see *Calibration* above. |
| Azimuth / altitude preload | 5′ | Preload only. Must be at least the real backlash. The overshoot leg can exceed *Maximum move* by this much. |
| Learned response / backlash / remember | on | See *Calibration* above. The backlash option applies to *Learned* only. |
| Max response scatter | 0.25 | Above this the axis is treated as slipping. |

## Known limitations

- The firmware has no AZ/ALT-only stop. On pause or stop the plugin sends `:Q#` (which stops every motor)
  and then `:MT1#` to turn tracking back on. A dedicated stop command in the firmware would be cleaner.
- Direction convention: TPPA's positive azimuth error means "move left", and positive altitude error means
  "move down" in the north. The plugin assumes, like OATControl, that `:MAZ`/`:MAL` with the error's sign
  corrects it. Confirm this on your mount.
- The N.I.N.A.-facing code has only been compile-checked against N.I.N.A.'s interfaces, not yet run inside
  N.I.N.A.

## Building and testing

```
dotnet build OatAutoPA/OatAutoPA.csproj          # Windows: copies the DLL into %localappdata%\NINA\Plugins\3.0.0
dotnet run --project OatAutoPA.Tests             # any OS, no NuGet packages needed
```

CI (`.github/workflows/build.yml`) runs the tests and builds the plugin on Windows for every push and pull
request, and uploads the zipped DLL as a build artifact. To publish a release, bump the version in
`OatAutoPA/Properties/AssemblyInfo.cs`, then tag and push:

```
git tag v0.1.0.0
git push origin v0.1.0.0
```

The workflow then attaches `OatAutoPA-v0.1.0.0.zip` to a GitHub release.

The tests run the controller against a simulated TPPA (following the bridge protocol) and a simulated mount
with a wrong gear ratio, backlash, optional slippage and reversed wiring. They check convergence, learning,
pause/stop, disconnects and that no measurement is ever taken while a motor is running.

## License

MPL-2.0. The bridge contract types mirror TPPA's (MPL-2.0, © Stefan Berg).
