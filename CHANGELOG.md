# Changelog

## 0.1.0.0 (unreleased)

- Backlash strategy option: **Preload** (default; overtravel-and-return so the gears always finish turning positive, 5′ per axis) or **Learned** (measured on reversals).
- First version: external correction controller for TPPA 2.2.8.0+ (bridge interface version 1).
- AZ/ALT moves through the N.I.N.A. telescope passthrough (`:MAZ`/`:MAL`, motion from `:GX`); TPPA capture is held while the motors run.
- Online calibration from the alignment run: gear response, backlash, slippage guard, direction detection; trusted values stored per profile.
