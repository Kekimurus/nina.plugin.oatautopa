// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using OatAutoPA.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OatAutoPA.Tests {

    /// <summary>In-process stand-in for N.I.N.A.'s message broker; round-trips every envelope through JSON.</summary>
    public sealed class FakeBroker : IBridgeTransport {
        public event Action<BridgeEnvelope> Received;
        public event Action<BridgeEnvelope> ToTppa;
        public readonly ConcurrentQueue<BridgeEnvelope> Sent = new ConcurrentQueue<BridgeEnvelope>();

        public Task PublishAsync(BridgeEnvelope envelope) {
            var copy = BridgeEnvelope.FromJson(envelope.ToJson());
            Sent.Enqueue(copy);
            ThreadPool.QueueUserWorkItem(_ => ToTppa?.Invoke(copy));
            return Task.CompletedTask;
        }

        public void FromTppa(BridgeEnvelope envelope) {
            var copy = BridgeEnvelope.FromJson(envelope.ToJson());
            Received?.Invoke(copy);
        }
    }

    /// <summary>
    /// OAT firmware + sky: a true polar error that moves when the AZ/ALT motors turn, with a wrong
    /// steps/degree (response), gear backlash, optional slippage and a configurable wiring direction.
    /// </summary>
    public sealed class SimMount : IOatMount {
        private readonly object gate = new object();
        private readonly Random random = new Random(1234);
        private DateTime azBusyUntil, altBusyUntil;
        private int azLastDir, altLastDir;

        public double AzError { get; set; } = 20;
        public double AltError { get; set; } = -12;
        public double AzResponse { get; set; } = 0.75;
        public double AltResponse { get; set; } = 1.2;
        public double AzBacklash { get; set; } = 2.0;
        public double AltBacklash { get; set; } = 1.0;
        /// <summary>+1: a positive command reduces a positive error (OAT convention). -1: wired backwards.</summary>
        public int AzWiring { get; set; } = 1;
        public int AltWiring { get; set; } = 1;
        /// <summary>Relative random slip per move (0 = repeatable gears).</summary>
        public double Slip { get; set; }
        public double MsPerArcMin { get; set; } = 3;

        public bool IsConnected { get; set; } = true;
        public int Stops;
        public readonly List<double> AzCommands = new List<double>();
        public readonly List<double> AltCommands = new List<double>();

        public bool Moving {
            get { lock (gate) { var now = DateTime.UtcNow; return now < azBusyUntil || now < altBusyUntil; } }
        }

        public Task MoveAzimuthAsync(double arcMinutes, CancellationToken token) {
            lock (gate) {
                AzCommands.Add(arcMinutes);
                AzError -= AzWiring * Effective(arcMinutes, ref azLastDir, AzBacklash) * AzResponse * SlipFactor();
                azBusyUntil = DateTime.UtcNow.AddMilliseconds(Math.Abs(arcMinutes) * MsPerArcMin + 20);
            }
            return Task.CompletedTask;
        }

        public Task MoveAltitudeAsync(double arcMinutes, CancellationToken token) {
            lock (gate) {
                AltCommands.Add(arcMinutes);
                AltError -= AltWiring * Effective(arcMinutes, ref altLastDir, AltBacklash) * AltResponse * SlipFactor();
                altBusyUntil = DateTime.UtcNow.AddMilliseconds(Math.Abs(arcMinutes) * MsPerArcMin + 20);
            }
            return Task.CompletedTask;
        }

        public Task<AzAltMotion?> GetMotionAsync(CancellationToken token) {
            if (!IsConnected) { return Task.FromResult<AzAltMotion?>(null); }
            lock (gate) {
                var now = DateTime.UtcNow;
                // Exercise the real parser with a firmware-shaped reply.
                var motion = $"--T{(now < azBusyUntil ? 'Z' : '-')}{(now < altBusyUntil ? 'A' : '-')}-";
                return Task.FromResult(GxStatusParser.Parse($"Tracking,{motion},1000,2000,300,120000,+450000,#"));
            }
        }

        /// <summary>What <c>:GVP#</c> answers; null = the driver rejects the command.</summary>
        public string ProductName { get; set; } = "OpenAstroMount";
        /// <summary>Number of identification calls that fail before answers come through (serial hiccup).</summary>
        public int FailIdentifications { get; set; }
        public int IdentifyCalls;

        public Task<MountIdentity> IdentifyAsync(CancellationToken token) {
            Interlocked.Increment(ref IdentifyCalls);
            if (FailIdentifications > 0) {
                FailIdentifications--;
                return Task.FromResult(new MountIdentity { DriverName = "Sim driver" });
            }
            var product = ProductName == null ? null : MountIdentity.CleanReply(ProductName + "#");
            return Task.FromResult(new MountIdentity { ProductName = product, FirmwareVersion = product == null ? null : "V1.13.14", DriverName = "Sim driver" });
        }

        public Task StopAzAltAsync() {
            lock (gate) {
                Stops++;
                azBusyUntil = altBusyUntil = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        private static double Effective(double command, ref int lastDir, double backlash) {
            var dir = Math.Sign(command);
            var size = Math.Abs(command);
            if (lastDir != 0 && dir != lastDir) { size = Math.Max(0, size - backlash); }
            if (lastDir == 0 && dir != 0) { size = Math.Max(0, size - backlash * 0.5); } // unknown starting play
            lastDir = dir == 0 ? lastDir : dir;
            return dir * size;
        }

        private double SlipFactor() => Slip <= 0 ? 1.0 : 1.0 + Slip * (2 * random.NextDouble() - 1);
    }

    /// <summary>Minimal TPPA following docs/external-correction.md: measures only when asked.</summary>
    public sealed class FakeTppa {
        private readonly FakeBroker broker;
        private readonly SimMount mount;
        private readonly Random noise = new Random(42);
        private readonly string controller;
        private readonly SemaphoreSlim answers = new SemaphoreSlim(0);
        private long seq;
        private int below;
        private string windowId;
        private string lastMeasurementId;
        private volatile BridgeEnvelope lastAnswer;

        public FakeTppa(FakeBroker broker, SimMount mount, string controller) {
            this.broker = broker;
            this.mount = mount;
            this.controller = controller;
            broker.ToTppa += OnCommand;
        }

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public double Tolerance { get; set; } = 1.0;
        public double NoiseArcMin { get; set; } = 0.05;
        public bool Northern { get; set; } = true;
        public int MeasurementsTaken;
        public int MeasurementsWhileMoving;
        public volatile bool ReadyReported;
        public volatile bool Achieved;
        public volatile string EndReason;
        public readonly ConcurrentQueue<string> Kinds = new ConcurrentQueue<string>();

        private void OnCommand(BridgeEnvelope e) {
            Kinds.Enqueue(e.Kind);
            switch (e.Kind) {
                case BridgeKind.Capabilities:
                    Send(BridgeKind.Capabilities, null, new BridgeCapabilitiesPayload { HeartbeatMs = 200, SilenceTimeoutMs = 1500, InterfaceVersion = 1 });
                    break;
                case BridgeKind.ControllerReady:
                    var r = e.PayloadAs<BridgeControllerReadyPayload>();
                    if (r.HardwareConnected == true && r.HardwareReady == true) { ReadyReported = true; }
                    break;
                case BridgeKind.BeginAdjustment:
                    var a = e.PayloadAs<BridgeAdjustmentRequestPayload>();
                    if (a.MeasurementId != lastMeasurementId) { break; } // refused
                    windowId ??= Guid.NewGuid().ToString("N");
                    Send(BridgeKind.AdjustmentGranted, SessionId, new BridgeAdjustmentGrantPayload { WindowId = windowId, MeasurementId = a.MeasurementId, SilenceTimeoutMs = 1500 });
                    break;
                case BridgeKind.RequestMeasurement:
                case BridgeKind.RequestCompletion:
                case BridgeKind.Fault:
                case BridgeKind.Cancel:
                case BridgeKind.Stopped:
                    lastAnswer = e;
                    answers.Release();
                    break;
            }
        }

        public void Send(string kind, string session, object payload) {
            broker.FromTppa(BridgeEnvelope.Create(kind, session, Interlocked.Increment(ref seq), controller, payload));
        }

        /// <summary>Runs a session until it ends or <paramref name="maxSamples"/> measurements were taken.</summary>
        public async Task<bool> RunAsync(int maxSamples = 40, int answerTimeoutMs = 5000, Func<int, Task> beforeAnswerWait = null) {
            Send(BridgeKind.SessionState, SessionId, new BridgeSessionStatePayload { State = BridgeState.Preparing });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ReadyReported && DateTime.UtcNow < deadline) { await Task.Delay(20); }
            if (!ReadyReported) { EndReason = "NoControllerReady"; return false; }

            for (var i = 0; i < maxSamples; i++) {
                if (mount.Moving) { MeasurementsWhileMoving++; }
                MeasurementsTaken++;
                var az = mount.AzError + Noise();
                var alt = mount.AltError + Noise();
                var total = Math.Sqrt(az * az + alt * alt);
                below = total <= Tolerance ? below + 1 : 0;
                lastMeasurementId = Guid.NewGuid().ToString("N");
                windowId = null;
                Send(BridgeKind.Measurement, SessionId, new BridgeMeasurementPayload {
                    MeasurementId = lastMeasurementId, SessionId = SessionId, SampleIndex = i, IsFirstMeasurement = i == 0,
                    Status = BridgeMeasurementStatus.Valid, AzimuthErrorArcMin = az, AltitudeErrorArcMin = alt, TotalErrorArcMin = total,
                    ToleranceArcMin = Tolerance, ToleranceReached = below > 0, AutoFinishConditionMet = below >= 2,
                    ConsecutiveBelowTolerance = below, Northern = Northern, TimestampUtc = DateTimeOffset.UtcNow
                });

                if (beforeAnswerWait != null) { await beforeAnswerWait(i); }
                if (!await answers.WaitAsync(answerTimeoutMs)) { EndReason = "NoAnswer"; return false; }
                var answer = lastAnswer;

                if (answer.Kind == BridgeKind.RequestCompletion) {
                    Achieved = below >= 2;
                    EndReason = "Completed";
                    Send(BridgeKind.SessionEnded, SessionId, new BridgeSessionEndedPayload { Reason = "Completed", Achieved = Achieved, TotalErrorArcMin = total, SamplesUsed = i + 1 });
                    return Achieved;
                }
                if (answer.Kind == BridgeKind.Fault || answer.Kind == BridgeKind.Cancel) {
                    EndReason = answer.Kind + ":" + (answer.PayloadAs<BridgeFaultPayload>()?.Reason);
                    Send(BridgeKind.SessionEnded, SessionId, new BridgeSessionEndedPayload { Reason = EndReason });
                    return false;
                }
                var request = answer.PayloadAs<BridgeMeasurementRequestPayload>();
                if (request?.SettleSeconds > 0) { await Task.Delay(10); } // settle (shortened)
            }
            EndReason = "MaxSamples";
            return false;
        }

        public BridgeEnvelope LastAnswer => lastAnswer;
        public SemaphoreSlim Answers => answers;

        private double Noise() => NoiseArcMin * (2 * noise.NextDouble() - 1);
    }

    public sealed class ConsoleLog : ILog {
        public bool Verbose { get; set; }
        public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
        public void Debug(string message) { Write("DBG", message); }
        public void Info(string message) { Write("INF", message); }
        public void Warning(string message) { Write("WRN", message); }
        public void Error(string message) { Write("ERR", message); }
        private void Write(string level, string message) {
            Lines.Enqueue($"{level} {message}");
            if (Verbose) { Console.WriteLine($"    {level} {message}"); }
        }
    }

    /// <summary>Settings store that writes direction flips back into the live settings, like the plugin's profile store.</summary>
    public sealed class MemoryStore : ICalibrationStore {
        private readonly ControllerSettings settings;
        public readonly Dictionary<Axis, AxisCalibrationSnapshot> Saved = new Dictionary<Axis, AxisCalibrationSnapshot>();
        public MemoryStore(ControllerSettings settings) { this.settings = settings; }
        public AxisCalibrationSnapshot Load(Axis axis) => Saved.TryGetValue(axis, out var s) ? s : new AxisCalibrationSnapshot();
        public void Save(Axis axis, AxisCalibrationSnapshot snapshot) { Saved[axis] = snapshot; }
        public void SaveDirectionInverted(Axis axis, bool inverted) {
            if (axis == Axis.Azimuth) { settings.InvertAzimuth = inverted; } else { settings.InvertAltitude = inverted; }
        }
    }
}
