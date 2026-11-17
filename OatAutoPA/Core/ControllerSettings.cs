// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

namespace OatAutoPA.Core {

    /// <summary>How gear backlash is handled.</summary>
    public enum BacklashStrategy {
        /// <summary>
        /// Every move ends turning in the positive (firmware) direction: a negative move overshoots by the
        /// preload and comes back. The play is always taken up on the same side, so no backlash value has to
        /// be known (only a preload at least as large). Same idea as TPPA's BacklashCompensationPlanner.
        /// </summary>
        Preload,

        /// <summary>Backlash is learned from reversals during the run and added to reversal moves.</summary>
        Learned
    }


    /// <summary>
    /// User-tunable behaviour. The controller reads a fresh copy for every measurement, so changes in
    /// the options page apply to the next correction.
    /// </summary>
    public sealed class ControllerSettings {
        /// <summary>Announce the controller to TPPA.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Fraction of the measured error corrected per step (safety factor against overshoot).</summary>
        public double Gain { get; set; } = 0.8;

        /// <summary>Largest single move per axis, arcminutes. Larger corrections are split over several steps.</summary>
        public double MaxMoveArcMin { get; set; } = 30;

        /// <summary>Above this error on an axis the controller refuses to move and asks for a manual adjustment.</summary>
        public double ManualAdjustLimitArcMin { get; set; } = 120;

        /// <summary>Axis errors below this are left alone.</summary>
        public double DeadbandArcMin { get; set; } = 0.1;

        /// <summary>Settle time TPPA waits after a move before it exposes.</summary>
        public double SettleSeconds { get; set; } = 1.5;

        public bool InvertAzimuth { get; set; }
        public bool InvertAltitude { get; set; }

        /// <summary>Flip an axis automatically when a move clearly makes its error worse.</summary>
        public bool AutoDetectDirection { get; set; } = true;

        /// <summary>Use the learned response (gear-ratio correction) to size moves.</summary>
        public bool UseLearnedResponse { get; set; } = true;

        public BacklashStrategy BacklashStrategy { get; set; } = BacklashStrategy.Preload;

        /// <summary>Preload overtravel for azimuth, arcminutes. Must be at least the real backlash.</summary>
        public double PreloadAzimuthArcMin { get; set; } = 5;

        /// <summary>Preload overtravel for altitude, arcminutes. Must be at least the real backlash.</summary>
        public double PreloadAltitudeArcMin { get; set; } = 5;

        /// <summary>Learned strategy only: add the learned backlash when an axis reverses.</summary>
        public bool UseBacklashCompensation { get; set; } = true;

        public double PreloadFor(Axis axis) => axis == Axis.Azimuth ? PreloadAzimuthArcMin : PreloadAltitudeArcMin;

        /// <summary>Remember trusted calibration between sessions.</summary>
        public bool PersistCalibration { get; set; } = true;

        /// <summary>
        /// Largest relative scatter (stddev / mean) of the response samples that is still trusted. Above it
        /// the mechanics are treated as non-repeatable (slippage) and the learned numbers are not applied.
        /// </summary>
        public double MaxResponseScatter { get; set; } = 0.25;

        /// <summary>Moves smaller than this are not used for learning: measurement noise would dominate.</summary>
        public double MinLearningMoveArcMin { get; set; } = 1.5;

        /// <summary>Measured changes smaller than this are treated as noise.</summary>
        public double NoiseFloorArcMin { get; set; } = 0.3;

        /// <summary>Give up when the motors are still running after this long.</summary>
        public double MoveTimeoutSeconds { get; set; } = 180;

        /// <summary>Fault the session after this many unusable measurements in a row.</summary>
        public int MaxConsecutiveUnusable { get; set; } = 10;

        // ---- Timing; only tests change these. ----
        public int AnnounceIntervalMs { get; set; } = 3000;
        public int MotionPollIntervalMs { get; set; } = 250;
        public int GrantTimeoutMs { get; set; } = 5000;
        public int DefaultKeepAliveMs { get; set; } = 2000;

        /// <summary>How soon a mount that failed identification is asked again.</summary>
        public int IdentityRecheckMs { get; set; } = 10000;

        public ControllerSettings Clone() => (ControllerSettings)MemberwiseClone();
    }
}
