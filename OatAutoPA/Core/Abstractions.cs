// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OatAutoPA.Core {

    /// <summary>Publishes envelopes on the command topic and delivers envelopes from the event topic.</summary>
    public interface IBridgeTransport {
        Task PublishAsync(BridgeEnvelope envelope);

        /// <summary>Raised for every envelope received on the event topic. Handlers must return quickly.</summary>
        event Action<BridgeEnvelope> Received;
    }

    /// <summary>Snapshot of the AZ/ALT motors, parsed from <c>:GX#</c>.</summary>
    public readonly struct AzAltMotion {
        public AzAltMotion(bool azimuthRunning, bool altitudeRunning) {
            AzimuthRunning = azimuthRunning;
            AltitudeRunning = altitudeRunning;
        }

        public bool AzimuthRunning { get; }
        public bool AltitudeRunning { get; }
        public bool AnyRunning => AzimuthRunning || AltitudeRunning;
    }

    /// <summary>The OAT/OAM firmware as the controller needs it. Arcminutes are the firmware's own unit.</summary>
    public interface IOatMount {
        bool IsConnected { get; }

        /// <summary><c>:MAZ&lt;arcmin&gt;#</c> - relative azimuth move, no reply.</summary>
        Task MoveAzimuthAsync(double arcMinutes, CancellationToken token);

        /// <summary><c>:MAL&lt;arcmin&gt;#</c> - relative altitude move, no reply.</summary>
        Task MoveAltitudeAsync(double arcMinutes, CancellationToken token);

        /// <summary>Motor state, or null when the status could not be read.</summary>
        Task<AzAltMotion?> GetMotionAsync(CancellationToken token);

        /// <summary>Stops the AZ/ALT motors and leaves the mount tracking.</summary>
        Task StopAzAltAsync();

        /// <summary>
        /// Asks the firmware what it is (<c>:GVP#</c>, <c>:GVN#</c>) and reports what driver N.I.N.A. is using.
        /// Must not throw: a driver that rejects the commands yields an empty product name.
        /// </summary>
        Task<MountIdentity> IdentifyAsync(CancellationToken token);
    }

    /// <summary>What the connected mount says it is.</summary>
    public sealed class MountIdentity {
        /// <summary><c>:GVP#</c> reply without the '#': "OpenAstroTracker" or "OpenAstroMount" on OAT firmware.</summary>
        public string ProductName { get; init; }

        /// <summary><c>:GVN#</c> reply without the '#', e.g. "V1.13.14". Informational.</summary>
        public string FirmwareVersion { get; init; }

        /// <summary>Driver as N.I.N.A. reports it (name / ASCOM ProgId). Only used in messages.</summary>
        public string DriverName { get; init; }

        /// <summary>
        /// True when the firmware answered as OpenAstroTech firmware. This, not the driver name, decides:
        /// it proves raw Meade commands sent through N.I.N.A. reach OAT firmware, whatever the transport.
        /// </summary>
        public bool IsOpenAstro =>
            !string.IsNullOrWhiteSpace(ProductName)
            && ProductName.Trim().StartsWith("OpenAstro", StringComparison.OrdinalIgnoreCase);

        public string Describe() {
            var product = string.IsNullOrWhiteSpace(ProductName) ? "no product name" : $"'{ProductName.Trim()}'";
            var version = string.IsNullOrWhiteSpace(FirmwareVersion) ? "" : $" {FirmwareVersion.Trim()}";
            var driver = string.IsNullOrWhiteSpace(DriverName) ? "" : $" via {DriverName.Trim()}";
            return product + version + driver;
        }

        /// <summary>Strips the terminator and rejects the OAT driver's "255" error marker and non-printable noise.</summary>
        public static string CleanReply(string reply) {
            if (string.IsNullOrWhiteSpace(reply)) { return null; }
            var text = reply.Trim().TrimEnd('#').Trim();
            if (text.Length == 0 || text == "255" || text.Length > 64) { return null; }
            foreach (var c in text) {
                if (c < 0x20 || c > 0x7E) { return null; }
            }
            return text;
        }
    }

    public interface ILog {
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>Keeps trusted calibration numbers between sessions (per N.I.N.A. profile).</summary>
    public interface ICalibrationStore {
        AxisCalibrationSnapshot Load(Axis axis);
        void Save(Axis axis, AxisCalibrationSnapshot snapshot);

        /// <summary>Called when direction detection decides an axis is reversed.</summary>
        void SaveDirectionInverted(Axis axis, bool inverted);
    }

    public enum Axis { Azimuth, Altitude }

    /// <summary>Learned numbers of one axis. Null members are unknown.</summary>
    public sealed class AxisCalibrationSnapshot {
        /// <summary>Measured error change per arcminute commanded (1.0 = firmware steps/degree are right).</summary>
        public double? Response { get; set; }

        /// <summary>Arcminutes (in command units) lost when the axis reverses.</summary>
        public double? BacklashArcMin { get; set; }
    }

    public sealed class NullCalibrationStore : ICalibrationStore {
        public AxisCalibrationSnapshot Load(Axis axis) => new AxisCalibrationSnapshot();
        public void Save(Axis axis, AxisCalibrationSnapshot snapshot) { }
        public void SaveDirectionInverted(Axis axis, bool inverted) { }
    }
}
