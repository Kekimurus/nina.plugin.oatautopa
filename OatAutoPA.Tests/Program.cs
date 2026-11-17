// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// Dependency-free test runner (no NuGet needed): `dotnet run --project OatAutoPA.Tests`.

using OatAutoPA.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace OatAutoPA.Tests {

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public static class Program {
        public static async Task<int> Main(string[] args) {
            var verbose = args.Contains("-v");
            var filter = args.FirstOrDefault(a => !a.StartsWith("-"));
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();

            var failed = 0;
            foreach (var test in tests) {
                Assert.Verbose = verbose;
                try {
                    var result = test.Invoke(null, null);
                    if (result is Task task) { await task; }
                    Console.WriteLine($"PASS {test.DeclaringType.Name}.{test.Name}");
                } catch (Exception ex) {
                    failed++;
                    var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                    Console.WriteLine($"FAIL {test.DeclaringType.Name}.{test.Name}: {inner?.Message}");
                    if (verbose) { Console.WriteLine(inner); }
                }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
            return failed == 0 ? 0 : 1;
        }
    }

    public static class Assert {
        public static bool Verbose;
        public static void True(bool condition, string message) {
            if (!condition) { throw new Exception(message); }
        }
        public static void Near(double expected, double actual, double tolerance, string what) {
            if (Math.Abs(expected - actual) > tolerance) {
                throw new Exception($"{what}: expected {expected:F3} ± {tolerance:F3}, got {actual:F3}");
            }
        }
    }

    public static class ParserTests {
        [Test]
        public static void IdleReply() {
            var m = GxStatusParser.Parse("Tracking,--T---,1000,2000,300,120000,+450000,#");
            Assert.True(m != null && !m.Value.AnyRunning, "idle expected");
        }

        [Test]
        public static void AzimuthAndAltitudeRunning() {
            var m = GxStatusParser.Parse("Tracking,--TzA-,1,2,3,4,5,#").Value;
            Assert.True(m.AzimuthRunning && m.AltitudeRunning, "both running expected");
        }

        [Test]
        public static void DriverErrorCodeIsUnknown() {
            Assert.True(GxStatusParser.Parse("255") == null, "driver error '255' must read as unknown");
            Assert.True(GxStatusParser.Parse("") == null, "empty must read as unknown");
            Assert.True(GxStatusParser.Parse("Tracking,--,1#") == null, "short motion field must read as unknown");
        }
    }

    public static class ContractTests {
        [Test]
        public static void ReadsNewtonsoftShapedEnvelope() {
            // Shape TPPA's Newtonsoft serializer produces (PascalCase, nulls, ISO dates, JObject payload).
            const string json = "{\"Version\":1,\"SessionId\":\"abc\",\"CommandId\":\"c1\",\"ReplyTo\":null,\"SequenceNumber\":7," +
                                "\"Kind\":\"Measurement\",\"IntendedRecipient\":\"OAT AutoPA\",\"Payload\":{\"MeasurementId\":\"m1\"," +
                                "\"SessionId\":\"abc\",\"WindowId\":null,\"SampleIndex\":3,\"IsFirstMeasurement\":false,\"Status\":\"Valid\"," +
                                "\"AzimuthErrorArcMin\":-4.25,\"AltitudeErrorArcMin\":1.5,\"TotalErrorArcMin\":4.5,\"ToleranceArcMin\":1.0," +
                                "\"ToleranceReached\":false,\"AutoFinishConditionMet\":false,\"ConsecutiveBelowTolerance\":0,\"Northern\":true," +
                                "\"ContinuousEstimation\":true,\"TimestampUtc\":\"2026-11-17T04:10:49.511+00:00\"}}";
            var e = BridgeEnvelope.FromJson(json);
            var m = e.PayloadAs<BridgeMeasurementPayload>();
            Assert.True(e.Kind == BridgeKind.Measurement && e.SequenceNumber == 7, "envelope fields");
            Assert.True(m.MeasurementId == "m1" && m.AzimuthErrorArcMin == -4.25 && m.Northern && m.SampleIndex == 3, "payload fields");
        }

        [Test]
        public static void WritesPascalCaseThatTppaReads() {
            var e = BridgeEnvelope.Create(BridgeKind.RequestMeasurement, "s", 1, BridgeContract.TppaRecipient,
                                          new BridgeMeasurementRequestPayload { WindowId = "w", StationaryAndSettled = true, SettleSeconds = 1.5 });
            var json = e.ToJson();
            Assert.True(json.Contains("\"Kind\":\"RequestMeasurement\"") && json.Contains("\"IntendedRecipient\":\"TPPA\""), json);
            Assert.True(json.Contains("\"WindowId\":\"w\"") && json.Contains("\"SettleSeconds\":1.5") && json.Contains("\"StationaryAndSettled\":true"), json);
        }

        [Test]
        public static void MalformedIsNull() {
            Assert.True(BridgeEnvelope.FromJson("{not json") == null, "malformed json");
            Assert.True(BridgeEnvelope.FromJson("{\"Kind\":\"X\"}").PayloadAs<BridgeMeasurementPayload>() == null, "missing payload");
        }
    }

    public static class CalibratorTests {
        private static readonly ControllerSettings S = new ControllerSettings { Gain = 1.0, UseBacklashCompensation = true, BacklashStrategy = BacklashStrategy.Learned };
        private static readonly ControllerSettings P = new ControllerSettings { Gain = 1.0, BacklashStrategy = BacklashStrategy.Preload, PreloadAzimuthArcMin = 5 };

        /// <summary>Executes a plan leg by leg, like the controller does, on a toy axis with play.</summary>
        private static double ApplyLegs(AxisMove move, double error, double response, double backlash, ref int lastDir) {
            foreach (var leg in new[] { move.FirstLegArcMin, move.PreloadReturnArcMin }) {
                if (leg == 0) { continue; }
                var dir = Math.Sign(leg);
                var size = Math.Abs(leg);
                if (lastDir != 0 && dir != lastDir) { size = Math.Max(0, size - backlash); }
                lastDir = dir;
                error -= dir * size * response;
            }
            return error;
        }

        [Test]
        public static void PreloadOvershootsNegativeMovesOnly() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var neg = cal.Plan(-4, 1, P);
            Assert.True(neg.PreloadReturnArcMin == 5 && neg.FirstLegArcMin == -9 && neg.MountArcMin == -4, "negative: -9 then +5");
            var pos = cal.Plan(4, 1, P);
            Assert.True(pos.PreloadReturnArcMin == 0 && pos.FirstLegArcMin == 4, "positive: single leg");
            var flipped = cal.Plan(4, -1, P);
            Assert.True(flipped.PreloadReturnArcMin == 5 && flipped.FirstLegArcMin == -9, "preload follows the firmware sign, not the error sign");
            Assert.True(cal.EffectiveBacklash(P) == 0, "no learned compensation with a preload");
        }

        [Test]
        public static void PreloadMakesEveryMoveACleanResponseSample() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var lastDir = 0;
            // Alternating errors: with Learned these would all be reversals; with a preload they all learn.
            var error = 8.0;
            foreach (var next in new[] { -6.0, 5.0, -7.0, 6.0, -5.0 }) {
                var move = cal.Plan(error, 1, P);
                var after = ApplyLegs(move, error, 0.8, 2.0, ref lastDir);
                var outcome = cal.Learn(move, error, after, P, out _);
                cal.Commit(move);
                error = next;
            }
            Assert.True(cal.ResponseState == TrustState.Trusted, "response trusted despite reversals: " + cal.Describe(P));
            Assert.Near(0.8, cal.EffectiveResponse(P), 0.01, "response measured exactly through 2' of play");
            Assert.True(cal.Plan(-3, 1, P).Kind == MoveKind.SameDirection, "preloaded moves count as same-direction");
        }

        /// <summary>Executes a plan on a toy axis: error -= response * (|sent| - play on reversal).</summary>
        private static double Apply(AxisMove move, double error, double response, double backlash, ref int lastDir) {
            var dir = Math.Sign(move.MountArcMin);
            var size = Math.Abs(move.MountArcMin);
            if (lastDir != 0 && dir != lastDir) { size = Math.Max(0, size - backlash); }
            lastDir = dir;
            return error - dir * size * response;
        }

        [Test]
        public static void LearnsResponseFromSameDirectionMoves() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var error = 40.0; var lastDir = 0;
            var settings = S.Clone(); settings.Gain = 0.5;
            for (var i = 0; i < 4; i++) {
                var move = cal.Plan(error, 1, settings);
                var after = Apply(move, error, 0.8, 0, ref lastDir);
                cal.Commit(move);
                cal.Learn(move, error, after, settings, out _);
                error = after;
            }
            Assert.True(cal.ResponseState == TrustState.Trusted, "response should be trusted, got " + cal.ResponseState);
            Assert.Near(0.8, cal.EffectiveResponse(settings), 0.01, "response");
            var next = cal.Plan(8, 1, settings);
            Assert.Near(4 / 0.8, next.Sent, 0.05, "moves are scaled by the learned response");
        }

        [Test]
        public static void LearnsBacklashFromReversals() {
            var cal = new AxisCalibrator(Axis.Altitude);
            cal.Seed(new AxisCalibrationSnapshot { Response = 1.0 });
            var settings = S.Clone();
            var lastDir = 0;
            // Establish direction and confirm the response.
            var error = 10.0;
            var m0 = cal.Plan(error, 1, settings); var a0 = Apply(m0, error, 1.0, 2.0, ref lastDir); cal.Commit(m0); cal.Learn(m0, error, a0, settings, out _);
            error = 8.0;
            var m1 = cal.Plan(error, 1, settings); var a1 = Apply(m1, error, 1.0, 2.0, ref lastDir); cal.Commit(m1); cal.Learn(m1, error, a1, settings, out _);
            Assert.True(cal.ResponseState == TrustState.Trusted, "response must be trusted before backlash is learned");
            // Alternate the sign of the error so every move reverses.
            foreach (var target in new[] { -6.0, 5.0, -4.0, 6.0 }) {
                error = target;
                var move = cal.Plan(error, 1, settings);
                var after = Apply(move, error, 1.0, 2.0, ref lastDir);
                cal.Commit(move);
                cal.Learn(move, error, after, settings, out var note);
            }
            Assert.True(cal.BacklashState(settings) == TrustState.Trusted, "backlash should be trusted");
            Assert.Near(2.0, cal.EffectiveBacklash(settings), 0.1, "backlash");
            var reversal = cal.Plan(-3, 1, settings);
            Assert.True(reversal.Kind == MoveKind.Reversal, "expected a reversal");
            Assert.Near(-5.0, reversal.Sent, 0.15, "reversal is compensated");
        }

        [Test]
        public static void SlippageIsNotTrusted() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var settings = S.Clone();
            var factors = new[] { 1.0, 0.4, 1.3, 0.6, 1.5 };
            var lastDir = 0; var error = 200.0;
            settings.MaxMoveArcMin = 1000;
            foreach (var f in factors) {
                var move = cal.Plan(error / 4, 1, settings);
                var after = Apply(move, error / 4, f, 0, ref lastDir);
                cal.Commit(move);
                cal.Learn(move, error / 4, after, settings, out _);
            }
            Assert.True(cal.ResponseState == TrustState.Unreliable, "scattered samples must be unreliable, got " + cal.ResponseState);
            Assert.True(cal.EffectiveResponse(settings) == 1.0, "unreliable response must not be applied");
            Assert.True(cal.EffectiveBacklash(settings) == 0, "no backlash on an unreliable axis");
        }

        [Test]
        public static void DetectsReversedAxis() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var move = cal.Plan(10, 1, S);
            cal.Commit(move);
            var outcome = cal.Learn(move, 10, 19, S, out _);
            Assert.True(outcome == LearnOutcome.DirectionReversed, "wrong-way move should flip, got " + outcome);
        }

        [Test]
        public static void SmallMovesAreNotLearned() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var move = cal.Plan(0.5, 1, S);
            Assert.True(cal.Learn(move, 0.5, 0.6, S, out _) == LearnOutcome.Skipped, "tiny moves are noise");
            Assert.True(cal.Plan(0.05, 1, S) == null, "inside the deadband");
        }

        [Test]
        public static void CapsLargeMoves() {
            var cal = new AxisCalibrator(Axis.Azimuth);
            var settings = S.Clone(); settings.MaxMoveArcMin = 30;
            var move = cal.Plan(-100, -1, settings);
            Assert.True(move.Capped && move.Sent == -30 && move.MountArcMin == 30, "capped and direction applied");
        }
    }

    public static class ControllerTests {
        private sealed class Rig : IDisposable {
            public readonly FakeBroker Broker = new FakeBroker();
            public readonly SimMount Mount = new SimMount();
            public readonly ConsoleLog Log = new ConsoleLog { Verbose = Assert.Verbose };
            public readonly ControllerSettings Settings = new ControllerSettings {
                SettleSeconds = 0.01, AnnounceIntervalMs = 100, MotionPollIntervalMs = 5, GrantTimeoutMs = 1000, DefaultKeepAliveMs = 100
            };
            public readonly MemoryStore Store;
            public readonly OatController Controller;
            public readonly FakeTppa Tppa;

            public Rig(Action<Rig> configure = null) {
                Store = new MemoryStore(Settings);
                configure?.Invoke(this);
                Controller = new OatController(Broker, Mount, () => Settings, Store, Log, "OAT AutoPA", "test");
                Tppa = new FakeTppa(Broker, Mount, "OAT AutoPA");
                Controller.Start();
            }

            public void Dispose() => Controller.Dispose();
        }

        [Test]
        public static async Task ConvergesAndNeverExposesWhileMoving() {
            using var rig = new Rig();
            var achieved = await rig.Tppa.RunAsync();
            Assert.True(achieved, $"alignment should be achieved, ended {rig.Tppa.EndReason} with AZ {rig.Mount.AzError:F2}' ALT {rig.Mount.AltError:F2}'");
            Assert.True(rig.Tppa.MeasurementsWhileMoving == 0, $"{rig.Tppa.MeasurementsWhileMoving} measurements taken while the motors were running");
            Assert.True(rig.Tppa.MeasurementsTaken <= 15, $"took {rig.Tppa.MeasurementsTaken} measurements");
            Assert.True(rig.Tppa.Kinds.Contains(BridgeKind.KeepAlive) || rig.Mount.AzCommands.Count > 0, "moves happened");
        }

        [Test]
        public static async Task LearnsGearRatioDuringTheRun() {
            using var rig = new Rig(r => { r.Mount.AzError = 60; r.Mount.AltError = 45; r.Settings.MaxMoveArcMin = 15; r.Settings.Gain = 0.6; r.Settings.BacklashStrategy = BacklashStrategy.Learned; });
            await rig.Tppa.RunAsync();
            Assert.True(rig.Controller.Azimuth.ResponseState == TrustState.Trusted, "AZ response should be trusted: " + rig.Controller.DescribeCalibration());
            Assert.Near(0.75, rig.Controller.Azimuth.EffectiveResponse(rig.Settings), 0.08, "AZ response");
            Assert.Near(1.2, rig.Controller.Altitude.EffectiveResponse(rig.Settings), 0.12, "ALT response");
            Assert.True(rig.Store.Saved.ContainsKey(Axis.Azimuth) && rig.Store.Saved[Axis.Azimuth].Response != null, "trusted values are persisted");
        }

        [Test]
        public static async Task PreloadConvergesThroughBacklashAndOvershoot() {
            // Gain above 1 makes it overshoot, so the axes reverse: the case backlash hurts most.
            using var rig = new Rig(r => {
                r.Mount.AzError = 25; r.Mount.AltError = -18; r.Mount.AzBacklash = 3; r.Mount.AltBacklash = 2;
                r.Settings.Gain = 1.0; r.Settings.BacklashStrategy = BacklashStrategy.Preload;
            });
            var achieved = await rig.Tppa.RunAsync();
            Assert.True(achieved, $"should converge, ended {rig.Tppa.EndReason}, AZ {rig.Mount.AzError:F2}' ALT {rig.Mount.AltError:F2}'");
            Assert.True(rig.Tppa.MeasurementsWhileMoving == 0, "no frame during either leg");
            // Every negative firmware command must be followed by a positive return of the preload.
            var alt = rig.Mount.AltCommands;
            for (var i = 0; i < alt.Count; i++) {
                if (alt[i] < 0) { Assert.True(i + 1 < alt.Count && Math.Abs(alt[i + 1] - 5) < 1e-9, $"ALT command {i} ({alt[i]:F2}) not followed by the +5' return"); }
            }
            Assert.True(alt.Any(c => c < 0), "the test should exercise a negative ALT move");
        }

        [Test]
        public static async Task PreloadLearnsResponseFromBothDirections() {
            using var rig = new Rig(r => {
                r.Mount.AzError = 40; r.Mount.AltError = -40; r.Mount.AzBacklash = 2; r.Mount.AltBacklash = 2;
                r.Settings.MaxMoveArcMin = 10; r.Settings.Gain = 0.6; r.Settings.BacklashStrategy = BacklashStrategy.Preload;
            });
            await rig.Tppa.RunAsync();
            // ALT only ever moves negative here: with Learned the first move's unknown play would be all it sees.
            Assert.Near(1.2, rig.Controller.Altitude.EffectiveResponse(rig.Settings), 0.1, "ALT response through the preload");
            Assert.Near(0.75, rig.Controller.Azimuth.EffectiveResponse(rig.Settings), 0.08, "AZ response");
        }

        [Test]
        public static async Task DetectsReversedWiringAndStillConverges() {
            using var rig = new Rig(r => { r.Mount.AzWiring = -1; });
            var achieved = await rig.Tppa.RunAsync();
            Assert.True(rig.Settings.InvertAzimuth, "AZ should have been flipped");
            Assert.True(achieved, $"should still converge, ended {rig.Tppa.EndReason}, AZ {rig.Mount.AzError:F2}'");
        }

        [Test]
        public static async Task SouthernHemisphereFlipsAltitude() {
            using var rig = new Rig(r => { r.Mount.AltWiring = -1; r.Settings.AutoDetectDirection = false; });
            rig.Tppa.Northern = false;
            var achieved = await rig.Tppa.RunAsync();
            Assert.True(achieved, $"south should converge without detection, ended {rig.Tppa.EndReason}, ALT {rig.Mount.AltError:F2}'");
        }

        [Test]
        public static async Task SlippingGearsDoNotGetTrusted() {
            using var rig = new Rig(r => { r.Mount.Slip = 0.6; r.Mount.AzError = 80; r.Settings.MaxMoveArcMin = 10; r.Settings.AutoDetectDirection = false; });
            await rig.Tppa.RunAsync(maxSamples: 25);
            var state = rig.Controller.Azimuth.ResponseState;
            Assert.True(state != TrustState.Trusted || rig.Controller.Azimuth.EffectiveResponse(rig.Settings) > 0.4,
                        "slipping AZ must not end up trusted with a wild value: " + rig.Controller.DescribeCalibration());
        }

        [Test]
        public static async Task ErrorTooLargeFaults() {
            using var rig = new Rig(r => r.Mount.AzError = 300);
            var achieved = await rig.Tppa.RunAsync();
            Assert.True(!achieved && rig.Tppa.EndReason == "Fault:ControllerFault", "expected a fault, got " + rig.Tppa.EndReason);
            Assert.True(rig.Mount.AzCommands.Count == 0, "must not move");
        }

        [Test]
        public static async Task DisconnectedMountFaults() {
            using var rig = new Rig();
            var run = rig.Tppa.RunAsync(beforeAnswerWait: i => { if (i == 1) { rig.Mount.IsConnected = false; } return Task.CompletedTask; });
            var achieved = await run;
            Assert.True(!achieved && rig.Tppa.EndReason.StartsWith("Fault"), "expected a fault, got " + rig.Tppa.EndReason);
            Assert.True(rig.Log.Lines.Any(l => l.Contains(BridgeReason.FirmwareDisconnected)), "reason FirmwareDisconnected");
        }

        [Test]
        public static async Task PauseStopsTheMoveAndAnswers() {
            using var rig = new Rig(r => { r.Mount.MsPerArcMin = 200; r.Mount.AzError = 30; });
            var tppa = rig.Tppa;
            tppa.Send(BridgeKind.SessionState, tppa.SessionId, new BridgeSessionStatePayload { State = BridgeState.Preparing });
            await WaitFor(() => tppa.ReadyReported, "readiness");
            tppa.Send(BridgeKind.Measurement, tppa.SessionId, new BridgeMeasurementPayload {
                MeasurementId = "m0", SessionId = tppa.SessionId, Status = BridgeMeasurementStatus.Valid,
                AzimuthErrorArcMin = 30, AltitudeErrorArcMin = 0, TotalErrorArcMin = 30, ToleranceArcMin = 1, Northern = true
            });
            // There is no FakeTppa loop here, so grant manually when asked.
            await WaitFor(() => rig.Broker.Sent.Any(e => e.Kind == BridgeKind.BeginAdjustment), "BeginAdjustment");
            tppa.Send(BridgeKind.AdjustmentGranted, tppa.SessionId, new BridgeAdjustmentGrantPayload { WindowId = "w1", MeasurementId = "m0" });
            await WaitFor(() => rig.Mount.Moving, "move started");
            tppa.Send(BridgeKind.PauseRequested, tppa.SessionId, new BridgePauseRequestPayload { Paused = true });
            await WaitFor(() => rig.Broker.Sent.Any(e => e.Kind == BridgeKind.RequestMeasurement), "answer after pause");
            var answer = rig.Broker.Sent.Last(e => e.Kind == BridgeKind.RequestMeasurement).PayloadAs<BridgeMeasurementRequestPayload>();
            Assert.True(answer.Reason == BridgeReason.Paused && answer.WindowId == "w1", "answer carries Paused and the window");
            Assert.True(rig.Mount.Stops >= 1 && !rig.Mount.Moving, "axes stopped");
        }

        [Test]
        public static async Task StopRequestIsAcknowledged() {
            using var rig = new Rig();
            var tppa = rig.Tppa;
            tppa.Send(BridgeKind.SessionState, tppa.SessionId, new BridgeSessionStatePayload { State = BridgeState.Preparing });
            await WaitFor(() => tppa.ReadyReported, "readiness");
            tppa.Send(BridgeKind.StopRequested, tppa.SessionId, new BridgeStopRequestPayload { Reason = BridgeReason.SessionTimeout, AckTimeoutMs = 5000 });
            await WaitFor(() => rig.Broker.Sent.Any(e => e.Kind == BridgeKind.Stopped), "Stopped");
            var stopped = rig.Broker.Sent.First(e => e.Kind == BridgeKind.Stopped).PayloadAs<BridgeStoppedPayload>();
            Assert.True(stopped.HardwareStopStatus == BridgeHardwareStopStatus.Ok && stopped.Reason == BridgeReason.SessionTimeout, "ack content");
        }

        [Test]
        public static async Task IgnoresTrafficForOtherControllers() {
            using var rig = new Rig();
            rig.Broker.FromTppa(BridgeEnvelope.Create(BridgeKind.Measurement, "other", 1, "MLAstroRPA", new BridgeMeasurementPayload {
                MeasurementId = "x", Status = BridgeMeasurementStatus.Valid, AzimuthErrorArcMin = 10, Northern = true
            }));
            await Task.Delay(200);
            Assert.True(rig.Controller.SessionId == null, "must not adopt another controller's session");
            Assert.True(!rig.Broker.Sent.Any(e => e.Kind == BridgeKind.BeginAdjustment), "must not answer");
        }

        [Test]
        public static async Task AnnouncesAndSaysGoodbyeWhenDisabled() {
            var rig = new Rig();
            await WaitFor(() => rig.Broker.Sent.Any(e => e.Kind == BridgeKind.Capabilities), "announce");
            var announce = rig.Broker.Sent.First(e => e.Kind == BridgeKind.Capabilities).PayloadAs<BridgeCapabilitiesAnnouncePayload>();
            Assert.True(announce.Controller == "OAT AutoPA" && announce.InterfaceVersion == 1, "announce content");
            rig.Settings.Enabled = false;
            await WaitFor(() => rig.Broker.Sent.Any(e => e.Kind == BridgeKind.Cancel), "goodbye");
            Assert.True(rig.Broker.Sent.First(e => e.Kind == BridgeKind.Cancel).PayloadAs<BridgeCancelPayload>().Reason == BridgeReason.BrokerDisabled, "BrokerDisabled");
            rig.Dispose();
        }

        private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 3000) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) { throw new Exception("timed out waiting for " + what); }
                await Task.Delay(10);
            }
        }
    }
}
