// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OatAutoPA.Core {

    /// <summary>How a planned move relates to the previous move of the same axis.</summary>
    public enum MoveKind {
        /// <summary>No earlier move is known: backlash state of the gears is unknown.</summary>
        Unknown,
        SameDirection,
        Reversal
    }

    public enum TrustState {
        /// <summary>Not enough samples yet; defaults are used.</summary>
        Learning,
        Trusted,
        /// <summary>Samples disagree (slippage or non-repeatable mechanics); learned numbers are not applied.</summary>
        Unreliable
    }

    /// <summary>One planned move of one axis.</summary>
    public sealed class AxisMove {
        public Axis Axis { get; init; }

        /// <summary>Correction wanted, in error arcminutes (gain applied).</summary>
        public double Desired { get; init; }

        /// <summary>
        /// What is commanded, in error arcminutes: <see cref="Desired"/> divided by the response in use,
        /// plus backlash compensation on a reversal, capped to the maximum move.
        /// </summary>
        public double Sent { get; init; }

        /// <summary>Net firmware move (<see cref="Sent"/> with the axis direction applied).</summary>
        public double MountArcMin { get; init; }

        /// <summary>
        /// Preload strategy: positive return leg after a negative move, in firmware arcminutes (0 = none).
        /// The move is executed as <see cref="FirstLegArcMin"/> followed by this, so the gears always finish
        /// turning positive and the net move is still <see cref="MountArcMin"/>.
        /// </summary>
        public double PreloadReturnArcMin { get; init; }

        /// <summary>First command sent to the firmware: the net move, overshooting by the preload if any.</summary>
        public double FirstLegArcMin => MountArcMin - PreloadReturnArcMin;

        public MoveKind Kind { get; init; }
        public double ResponseUsed { get; init; }
        public double BacklashApplied { get; init; }
        public bool Capped { get; init; }

        public int MountSign => Math.Sign(MountArcMin);
    }

    public enum LearnOutcome {
        Skipped,
        ResponseSample,
        BacklashSample,
        /// <summary>The move clearly made the error worse: the axis direction is reversed.</summary>
        DirectionReversed
    }

    /// <summary>
    /// Learns, from the polar error TPPA measures before and after each move, how far the axis really
    /// moves per commanded arcminute (the response, i.e. a correction to the firmware's steps/degree) and
    /// how much travel is lost when it reverses (backlash). No dedicated calibration frames are taken:
    /// every correction of the alignment run doubles as a calibration sample.
    ///
    /// Model, in error arcminutes, for a move that sends <c>s</c> and changes the error by <c>d</c> in
    /// the intended direction: same direction <c>d = r * |s|</c>; after a reversal <c>d = r * (|s| - b)</c>.
    /// With the preload strategy every move ends turning positive, so the play is always taken up on the
    /// same side and every move after the first behaves like a same-direction move: <c>d = r * |s|</c>
    /// even across sign changes, provided the preload is at least the real backlash.
    /// Numbers are only applied once they repeat; samples that scatter mark the axis unreliable (slippage)
    /// and the controller falls back to plain, uncompensated moves.
    /// </summary>
    public sealed class AxisCalibrator {
        private const int Window = 5;
        private const double MinPlausibleResponse = 0.25;
        private const double MaxPlausibleResponse = 4.0;

        private readonly List<double> responseSamples = new List<double>();
        private readonly List<double> backlashSamples = new List<double>();
        private readonly object gate = new object();

        public AxisCalibrator(Axis axis) {
            Axis = axis;
        }

        public Axis Axis { get; }

        /// <summary>Sign of the last executed move as the motor saw it, or null when unknown.</summary>
        public int? LastMountSign { get; private set; }

        public void Seed(AxisCalibrationSnapshot snapshot) {
            if (snapshot == null) { return; }
            lock (gate) {
                if (snapshot.Response is double r && r >= MinPlausibleResponse && r <= MaxPlausibleResponse) {
                    responseSamples.Add(r);
                }
                if (snapshot.BacklashArcMin is double b && b >= 0) {
                    backlashSamples.Add(b);
                }
            }
        }

        public void Reset() {
            lock (gate) {
                responseSamples.Clear();
                backlashSamples.Clear();
                LastMountSign = null;
            }
        }

        /// <summary>Forgets the direction of the last move, e.g. after a move was interrupted.</summary>
        public void ForgetLastDirection() {
            lock (gate) { LastMountSign = null; }
        }

        public TrustState ResponseState {
            get { lock (gate) { return EvaluateResponse(out _); } }
        }

        /// <summary>The response applied to moves (1.0 unless trusted).</summary>
        public double EffectiveResponse(ControllerSettings settings) {
            lock (gate) {
                return settings.UseLearnedResponse && EvaluateResponse(out var mean) == TrustState.Trusted ? mean : 1.0;
            }
        }

        public TrustState BacklashState(ControllerSettings settings) {
            lock (gate) { return EvaluateBacklash(settings, out _); }
        }

        /// <summary>The backlash added on reversals (0 unless trusted and the response is not unreliable).</summary>
        public double EffectiveBacklash(ControllerSettings settings) {
            lock (gate) {
                if (!settings.UseBacklashCompensation || UsesPreload(settings)) { return 0; }
                if (EvaluateResponse(out _) == TrustState.Unreliable) { return 0; }
                return EvaluateBacklash(settings, out var median) == TrustState.Trusted ? median : 0;
            }
        }

        /// <summary>
        /// Plans the move for a measured error. Returns null when the error is inside the deadband.
        /// <paramref name="directionSign"/> is +1 or -1: the firmware sign that reduces a positive error.
        /// </summary>
        public AxisMove Plan(double errorArcMin, int directionSign, ControllerSettings settings) {
            if (double.IsNaN(errorArcMin) || Math.Abs(errorArcMin) < settings.DeadbandArcMin) { return null; }

            var desired = settings.Gain * errorArcMin;
            var response = EffectiveResponse(settings);
            var baseMove = desired / response;
            var mountSign = Math.Sign(baseMove) * directionSign;
            var preload = UsesPreload(settings);

            MoveKind kind;
            lock (gate) {
                // With a preload the gears always finish turning positive, so once one move has been made the
                // play is taken up on the same side before every move: each one behaves like a same-direction move.
                kind = LastMountSign == null ? MoveKind.Unknown
                     : preload || LastMountSign == mountSign ? MoveKind.SameDirection
                     : MoveKind.Reversal;
            }

            var backlash = !preload && kind == MoveKind.Reversal ? EffectiveBacklash(settings) : 0;
            var sent = baseMove + Math.Sign(baseMove) * backlash;

            var capped = false;
            if (Math.Abs(sent) > settings.MaxMoveArcMin) {
                sent = Math.Sign(sent) * settings.MaxMoveArcMin;
                capped = true;
            }

            var mountArcMin = sent * directionSign;
            return new AxisMove {
                Axis = Axis,
                Desired = desired,
                Sent = sent,
                MountArcMin = mountArcMin,
                PreloadReturnArcMin = preload && mountArcMin < 0 ? settings.PreloadFor(Axis) : 0,
                Kind = kind,
                ResponseUsed = response,
                BacklashApplied = backlash,
                Capped = capped
            };
        }

        /// <summary>Records that a planned move was executed to the end.</summary>
        public void Commit(AxisMove move) {
            if (move == null || move.MountSign == 0) { return; }
            lock (gate) { LastMountSign = move.PreloadReturnArcMin > 0 ? 1 : move.MountSign; }
        }

        /// <summary>
        /// Learns from the error measured before and after an executed move. Call with valid measurements only.
        /// </summary>
        public LearnOutcome Learn(AxisMove move, double errorBefore, double errorAfter, ControllerSettings settings, out string note) {
            note = null;
            if (move == null) { return LearnOutcome.Skipped; }

            var sentSize = Math.Abs(move.Sent);
            if (sentSize < settings.MinLearningMoveArcMin) {
                note = $"move of {Fmt(move.Sent)}' too small to learn from";
                return LearnOutcome.Skipped;
            }

            // Progress: how far the error moved in the intended direction.
            var progress = (errorBefore - errorAfter) * Math.Sign(move.Sent);

            lock (gate) {
                var responseState = EvaluateResponse(out var meanResponse);

                // A clearly wrong-way move before anything is trusted means the axis is reversed.
                // Once the response is trusted, a wrong-way move is treated as a sample instead (slippage).
                var wrongWayThreshold = Math.Max(settings.NoiseFloorArcMin, 0.5 * sentSize * Math.Min(1.0, move.ResponseUsed));
                if (settings.AutoDetectDirection && responseState != TrustState.Trusted && progress < -wrongWayThreshold) {
                    responseSamples.Clear();
                    backlashSamples.Clear();
                    note = $"error moved {Fmt(-progress)}' the wrong way for a {Fmt(move.Sent)}' move";
                    return LearnOutcome.DirectionReversed;
                }

                switch (move.Kind) {
                    case MoveKind.SameDirection: {
                        var sample = progress / sentSize;
                        Push(responseSamples, sample);
                        note = $"response sample {Fmt(sample, 3)} (moved {Fmt(progress)}' for {Fmt(move.Sent)}')";
                        return LearnOutcome.ResponseSample;
                    }

                    case MoveKind.Reversal: {
                        if (responseState != TrustState.Trusted) {
                            note = "reversal, but the response is not trusted yet: backlash not learned";
                            return LearnOutcome.Skipped;
                        }
                        var sample = Math.Clamp(sentSize - progress / meanResponse, 0, sentSize);
                        Push(backlashSamples, sample);
                        note = $"backlash sample {Fmt(sample)}' (moved {Fmt(progress)}' for {Fmt(move.Sent)}' incl. {Fmt(move.BacklashApplied)}' compensation)";
                        return LearnOutcome.BacklashSample;
                    }

                    default:
                        note = "first move of the axis: gear play unknown, not learned";
                        return LearnOutcome.Skipped;
                }
            }
        }

        public AxisCalibrationSnapshot TrustedSnapshot(ControllerSettings settings) {
            lock (gate) {
                var snapshot = new AxisCalibrationSnapshot();
                if (EvaluateResponse(out var mean) == TrustState.Trusted) {
                    snapshot.Response = mean;
                    if (EvaluateBacklash(settings, out var median) == TrustState.Trusted) {
                        snapshot.BacklashArcMin = median;
                    }
                }
                return snapshot;
            }
        }

        public string Describe(ControllerSettings settings) {
            lock (gate) {
                var rs = EvaluateResponse(out var mean);
                var bs = EvaluateBacklash(settings, out var median);
                var response = rs == TrustState.Trusted ? $"response {Fmt(mean, 3)}" : $"response {rs.ToString().ToLowerInvariant()} ({responseSamples.Count} samples)";
                var backlash = UsesPreload(settings) ? $"preload {Fmt(settings.PreloadFor(Axis))}'"
                             : bs == TrustState.Trusted ? $"backlash {Fmt(median)}'"
                             : $"backlash {bs.ToString().ToLowerInvariant()} ({backlashSamples.Count} samples)";
                return $"{Axis}: {response}, {backlash}";
            }
        }

        private bool UsesPreload(ControllerSettings settings) =>
            settings.BacklashStrategy == BacklashStrategy.Preload && settings.PreloadFor(Axis) > 0;

        private TrustState EvaluateResponse(out double mean) {
            mean = 1.0;
            if (responseSamples.Count < 2) { return TrustState.Learning; }

            mean = responseSamples.Average();
            var std = StdDev(responseSamples, mean);
            var consistent = responseSamples.All(s => s > 0)
                             && mean >= MinPlausibleResponse && mean <= MaxPlausibleResponse
                             && std / mean <= MaxResponseScatter;
            if (consistent) { return TrustState.Trusted; }

            mean = 1.0;
            // Two samples that disagree could be one outlier: wait for a third before calling it slippage.
            return responseSamples.Count >= 3 ? TrustState.Unreliable : TrustState.Learning;
        }

        private TrustState EvaluateBacklash(ControllerSettings settings, out double median) {
            median = 0;
            if (backlashSamples.Count < 2) { return TrustState.Learning; }

            var sorted = backlashSamples.OrderBy(s => s).ToList();
            median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
            var spread = sorted[^1] - sorted[0];
            if (spread <= Math.Max(2 * settings.NoiseFloorArcMin, 0.5 * median)) { return TrustState.Trusted; }

            median = 0;
            return backlashSamples.Count >= 3 ? TrustState.Unreliable : TrustState.Learning;
        }

        /// <summary>Scatter limit for the response samples (see <see cref="ControllerSettings.MaxResponseScatter"/>).</summary>
        public double MaxResponseScatter { get; set; } = 0.25;

        private static void Push(List<double> samples, double value) {
            samples.Add(value);
            while (samples.Count > Window) { samples.RemoveAt(0); }
        }

        private static double StdDev(IReadOnlyCollection<double> values, double mean) {
            if (values.Count < 2) { return 0; }
            var sum = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sum / (values.Count - 1));
        }

        private static string Fmt(double value, int decimals = 2) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
