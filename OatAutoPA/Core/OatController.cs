// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OatAutoPA.Core {

    /// <summary>
    /// External correction controller for TPPA's bridge (docs/external-correction.md in the TPPA repo).
    /// TPPA measures; this class decides and executes the AZ/ALT moves on the OAT/OAM firmware, holding
    /// TPPA's capture while the motors turn so no frame is wasted on a moving field.
    ///
    /// Threading: broker callbacks only update state and enqueue; measurements are handled one at a time
    /// on a worker; keep-alives come from their own timer so a long move never starves the watchdog.
    /// </summary>
    public sealed class OatController : IDisposable {
        private readonly IBridgeTransport transport;
        private readonly IOatMount mount;
        private readonly Func<ControllerSettings> settingsSource;
        private readonly ICalibrationStore store;
        private readonly ILog log;
        private readonly Channel<BridgeMeasurementPayload> measurements = Channel.CreateUnbounded<BridgeMeasurementPayload>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object gate = new object();

        private Timer announceTimer;
        private Timer readinessTimer;
        private Task worker;
        private long sequence;
        private int readinessBusy;
        private bool started;
        private bool disposed;

        // Announcement state.
        private bool announcing;

        // Values from TPPA's Capabilities reply.
        private int keepAliveMs;

        // Session state (guarded by gate).
        private string sessionId;
        private bool handedOver;
        private bool paused;
        private bool stopping;
        private bool faulted;
        private CancellationTokenSource moveCts;
        private TaskCompletionSource<BridgeAdjustmentGrantPayload> pendingGrant;
        private string pendingGrantMeasurementId;
        private MoveRecord lastMove;
        private int consecutiveUnusable;
        private bool? reportedConnected;
        private bool? reportedReady;

        public OatController(IBridgeTransport transport,
                             IOatMount mount,
                             Func<ControllerSettings> settingsSource,
                             ICalibrationStore store,
                             ILog log,
                             string controllerName,
                             string controllerVersion) {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
            this.settingsSource = settingsSource ?? throw new ArgumentNullException(nameof(settingsSource));
            this.store = store ?? new NullCalibrationStore();
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            ControllerName = controllerName;
            ControllerVersion = controllerVersion;

            var initial = Settings();
            keepAliveMs = initial.DefaultKeepAliveMs;
            if (initial.PersistCalibration) {
                Azimuth.Seed(this.store.Load(Axis.Azimuth));
                Altitude.Seed(this.store.Load(Axis.Altitude));
            }
        }

        /// <summary>Name TPPA lists and addresses this controller by.</summary>
        public string ControllerName { get; }

        public string ControllerVersion { get; }

        public AxisCalibrator Azimuth { get; } = new AxisCalibrator(Axis.Azimuth);
        public AxisCalibrator Altitude { get; } = new AxisCalibrator(Axis.Altitude);

        /// <summary>Last human-readable status line.</summary>
        public string Status { get; private set; } = "Idle";

        public event Action<string> StatusChanged;

        public string SessionId {
            get { lock (gate) { return sessionId; } }
        }

        public void Start() {
            lock (gate) {
                if (started || disposed) { return; }
                started = true;
            }
            transport.Received += OnReceived;
            worker = Task.Run(WorkerLoopAsync);
            var settings = Settings();
            announceTimer = new Timer(_ => OnAnnounceTick(), null, 0, settings.AnnounceIntervalMs);
            readinessTimer = new Timer(_ => _ = OnReadinessTickAsync(), null, 1000, 1000);
            SetStatus("Waiting for TPPA");
        }

        /// <summary>Forgets everything learned (and the stored copy).</summary>
        public void ResetCalibration() {
            Azimuth.Reset();
            Altitude.Reset();
            store.Save(Axis.Azimuth, new AxisCalibrationSnapshot());
            store.Save(Axis.Altitude, new AxisCalibrationSnapshot());
            log.Info("[OAT] Learned calibration reset.");
        }

        public string DescribeCalibration() {
            var settings = Settings();
            return Azimuth.Describe(settings) + "; " + Altitude.Describe(settings);
        }

        public void Dispose() {
            lock (gate) {
                if (disposed) { return; }
                disposed = true;
            }
            announceTimer?.Dispose();
            readinessTimer?.Dispose();
            CancelMove();
            if (announcing) {
                // Say goodbye so TPPA drops us from its list at once.
                PublishFireAndForget(BridgeKind.Cancel, new BridgeCancelPayload { Reason = BridgeReason.BrokerDisabled, Note = "Plugin unloaded" });
            }
            transport.Received -= OnReceived;
            measurements.Writer.TryComplete();
            lifetime.Cancel();
        }

        // ------------------------------------------------------------------ receive path

        private void OnReceived(BridgeEnvelope envelope) {
            try {
                Dispatch(envelope);
            } catch (Exception ex) {
                log.Error($"[OAT] Failed to handle '{envelope?.Kind}': {ex.Message}");
            }
        }

        private void Dispatch(BridgeEnvelope envelope) {
            if (envelope == null || disposed) { return; }
            if (envelope.Version != BridgeContract.InterfaceVersion) {
                log.Debug($"[OAT] Ignoring interface version {envelope.Version}.");
                return;
            }
            if (!string.IsNullOrEmpty(envelope.IntendedRecipient)
                && !string.Equals(envelope.IntendedRecipient, ControllerName, StringComparison.OrdinalIgnoreCase)) {
                return; // TPPA talks to another controller.
            }

            switch (envelope.Kind) {
                case BridgeKind.Capabilities:
                    OnCapabilities(envelope.PayloadAs<BridgeCapabilitiesPayload>());
                    return;
                case BridgeKind.SessionState:
                    OnSessionState(envelope);
                    return;
            }

            if (!BelongsToSession(envelope)) { return; }

            switch (envelope.Kind) {
                case BridgeKind.Measurement:
                    OnMeasurement(envelope);
                    return;
                case BridgeKind.AdjustmentGranted:
                    OnGranted(envelope.PayloadAs<BridgeAdjustmentGrantPayload>());
                    return;
                case BridgeKind.PauseRequested:
                    OnPause(envelope.PayloadAs<BridgePauseRequestPayload>());
                    return;
                case BridgeKind.StopRequested:
                    _ = OnStopAsync(envelope.PayloadAs<BridgeStopRequestPayload>());
                    return;
                case BridgeKind.SessionEnded:
                    OnSessionEnded(envelope.PayloadAs<BridgeSessionEndedPayload>());
                    return;
                default:
                    log.Debug($"[OAT] Ignoring '{envelope.Kind}'.");
                    return;
            }
        }

        private bool BelongsToSession(BridgeEnvelope envelope) {
            lock (gate) {
                if (string.IsNullOrEmpty(envelope.SessionId)) { return true; }
                if (sessionId == null) {
                    // Joined late (e.g. plugin reloaded mid-run): adopt the session.
                    BeginSessionLocked(envelope.SessionId);
                    return true;
                }
                return string.Equals(sessionId, envelope.SessionId, StringComparison.Ordinal);
            }
        }

        private void OnCapabilities(BridgeCapabilitiesPayload caps) {
            if (caps == null) { return; }
            var settings = Settings();
            var heartbeat = caps.HeartbeatMs > 0 ? caps.HeartbeatMs : settings.DefaultKeepAliveMs;
            var silenceThird = caps.SilenceTimeoutMs > 0 ? caps.SilenceTimeoutMs / 3 : heartbeat;
            keepAliveMs = Math.Max(100, Math.Min(heartbeat, silenceThird));
        }

        private void OnSessionState(BridgeEnvelope envelope) {
            var state = envelope.PayloadAs<BridgeSessionStatePayload>();
            if (state == null || string.IsNullOrEmpty(envelope.SessionId)) { return; }

            bool newSession;
            lock (gate) {
                newSession = !string.Equals(sessionId, envelope.SessionId, StringComparison.Ordinal);
                if (newSession && state.State != BridgeState.Ended) {
                    BeginSessionLocked(envelope.SessionId);
                }
            }

            if (state.State == BridgeState.Preparing) {
                // TPPA asks for readiness before the reference frames: answer right away.
                _ = ReportReadinessAsync(force: true);
                if (newSession) { SetStatus("TPPA session starting"); }
            } else if (state.State == BridgeState.Ended && !newSession) {
                EndSession("session ended");
            }
        }

        private void OnMeasurement(BridgeEnvelope envelope) {
            var m = envelope.PayloadAs<BridgeMeasurementPayload>();
            if (m == null) { return; }
            lock (gate) {
                handedOver = true;
                if (faulted) { return; }
            }
            measurements.Writer.TryWrite(m);
        }

        private void OnGranted(BridgeAdjustmentGrantPayload grant) {
            if (grant == null) { return; }
            TaskCompletionSource<BridgeAdjustmentGrantPayload> waiter;
            lock (gate) {
                waiter = pendingGrant;
                if (waiter == null) { return; }
                if (!string.IsNullOrEmpty(grant.MeasurementId) && !string.IsNullOrEmpty(pendingGrantMeasurementId)
                    && !string.Equals(grant.MeasurementId, pendingGrantMeasurementId, StringComparison.Ordinal)) {
                    return;
                }
            }
            waiter.TrySetResult(grant);
        }

        private void OnPause(BridgePauseRequestPayload pause) {
            if (pause == null) { return; }
            lock (gate) { paused = pause.Paused; }
            if (pause.Paused) {
                log.Info("[OAT] TPPA paused: stopping any move.");
                CancelMove();
                SetStatus("Paused");
            } else {
                log.Info("[OAT] TPPA resumed.");
                _ = ReportReadinessAsync(force: true);
                SetStatus("Resumed");
            }
        }

        private async Task OnStopAsync(BridgeStopRequestPayload stop) {
            lock (gate) { stopping = true; }
            CancelMove();
            var status = BridgeHardwareStopStatus.Ok;
            string detail = null;
            try {
                if (mount.IsConnected) {
                    await mount.StopAzAltAsync().ConfigureAwait(false);
                } else {
                    status = BridgeHardwareStopStatus.Unknown;
                    detail = "mount not connected";
                }
            } catch (Exception ex) {
                status = BridgeHardwareStopStatus.Fault;
                detail = ex.Message;
            }
            Azimuth.ForgetLastDirection();
            Altitude.ForgetLastDirection();
            await PublishAsync(BridgeKind.Stopped, new BridgeStoppedPayload { Reason = stop?.Reason, HardwareStopStatus = status, Detail = detail }).ConfigureAwait(false);
            SetStatus($"Stopped ({stop?.Reason})");
        }

        private void OnSessionEnded(BridgeSessionEndedPayload ended) {
            var result = ended == null ? "" :
                $"{(ended.Achieved ? "achieved" : "not achieved")} ({ended.Reason}), total error {F(ended.TotalErrorArcMin)}' after {ended.SamplesUsed} samples";
            EndSession(result);
        }

        // ------------------------------------------------------------------ session bookkeeping

        private void BeginSessionLocked(string id) {
            sessionId = id;
            handedOver = false;
            paused = false;
            stopping = false;
            faulted = false;
            lastMove = null;
            consecutiveUnusable = 0;
            reportedConnected = null;
            reportedReady = null;
            log.Info($"[OAT] Joined TPPA session {id}.");
        }

        private void EndSession(string result) {
            string id;
            lock (gate) {
                id = sessionId;
                sessionId = null;
                handedOver = false;
                lastMove = null;
            }
            CancelMove();
            // Drop measurements that were queued for the old session.
            while (measurements.Reader.TryRead(out _)) { }
            PersistCalibration();
            log.Info($"[OAT] Session {id} ended: {result}. {DescribeCalibration()}");
            SetStatus($"Session ended: {result}");
        }

        private void PersistCalibration() {
            var settings = Settings();
            if (!settings.PersistCalibration) { return; }
            foreach (var axis in new[] { Azimuth, Altitude }) {
                var snapshot = axis.TrustedSnapshot(settings);
                if (snapshot.Response != null) {
                    store.Save(axis.Axis, snapshot);
                }
            }
        }

        // ------------------------------------------------------------------ measurement handling

        private async Task WorkerLoopAsync() {
            try {
                while (await measurements.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false)) {
                    while (measurements.Reader.TryRead(out var m)) {
                        try {
                            await HandleMeasurementAsync(m).ConfigureAwait(false);
                        } catch (Exception ex) {
                            log.Error($"[OAT] Measurement {m.MeasurementId} failed: {ex}");
                        }
                    }
                }
            } catch (OperationCanceledException) {
            }
        }

        private async Task HandleMeasurementAsync(BridgeMeasurementPayload m) {
            var settings = Settings();
            Azimuth.MaxResponseScatter = settings.MaxResponseScatter;
            Altitude.MaxResponseScatter = settings.MaxResponseScatter;

            lock (gate) {
                if (faulted || stopping || sessionId == null) { return; }
                if (paused) {
                    log.Debug($"[OAT] Measurement {m.MeasurementId} arrived while paused: TPPA ended that interaction.");
                    return;
                }
            }

            log.Info($"[OAT] Measurement #{m.SampleIndex} {m.Status}: AZ {F(m.AzimuthErrorArcMin)}' ALT {F(m.AltitudeErrorArcMin)}' total {F(m.TotalErrorArcMin)}' (tolerance {F(m.ToleranceArcMin)}')");

            if (!mount.IsConnected) {
                await FaultAsync(BridgeReason.FirmwareDisconnected, "The mount is not connected.").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(m.Status, BridgeMeasurementStatus.Valid, StringComparison.Ordinal)) {
                int count;
                lock (gate) { count = ++consecutiveUnusable; }
                if (count >= settings.MaxConsecutiveUnusable) {
                    await FaultAsync(BridgeReason.ControllerFault, $"{count} unusable measurements in a row.").ConfigureAwait(false);
                    return;
                }
                SetStatus($"Measurement {m.Status}, asking for another");
                await AnswerMeasurementAsync(null, BridgeReason.Unusable, 0).ConfigureAwait(false);
                return;
            }

            lock (gate) { consecutiveUnusable = 0; }
            LearnFromLastMove(m, settings);

            if (m.AutoFinishConditionMet) {
                SetStatus($"Within tolerance ({F(m.TotalErrorArcMin)}'), asking TPPA to finish");
                await PublishAsync(BridgeKind.RequestCompletion, new BridgeCompletionRequestPayload {
                    Reason = BridgeReason.CompletionRequested,
                    ConsecutiveBelowTolerance = m.ConsecutiveBelowTolerance
                }).ConfigureAwait(false);
                return;
            }

            if (m.ToleranceReached) {
                // Do not touch the axes: TPPA needs consecutive samples below tolerance to finish.
                SetStatus($"Within tolerance ({F(m.TotalErrorArcMin)}'), confirming");
                await AnswerMeasurementAsync(null, BridgeReason.ConfirmTolerance, 0).ConfigureAwait(false);
                return;
            }

            if (Math.Abs(m.AzimuthErrorArcMin) > settings.ManualAdjustLimitArcMin
                || Math.Abs(m.AltitudeErrorArcMin) > settings.ManualAdjustLimitArcMin) {
                await FaultAsync(BridgeReason.ControllerFault,
                    $"Polar error too large for automatic adjustment (AZ {F(m.AzimuthErrorArcMin)}', ALT {F(m.AltitudeErrorArcMin)}'). " +
                    $"Adjust manually to within {F(settings.ManualAdjustLimitArcMin)}' and start again.").ConfigureAwait(false);
                return;
            }

            var azMove = Azimuth.Plan(m.AzimuthErrorArcMin, DirectionSign(Axis.Azimuth, m.Northern, settings), settings);
            var altMove = Altitude.Plan(m.AltitudeErrorArcMin, DirectionSign(Axis.Altitude, m.Northern, settings), settings);

            if (azMove == null && altMove == null) {
                await AnswerMeasurementAsync(null, BridgeReason.NoMoveNeeded, 0).ConfigureAwait(false);
                return;
            }

            var grant = await RequestWindowAsync(m, azMove, altMove, settings).ConfigureAwait(false);
            if (grant == null) {
                log.Warning($"[OAT] TPPA did not grant a capture window for measurement {m.MeasurementId}; not moving.");
                await AnswerMeasurementAsync(null, BridgeReason.NotGranted, 0).ConfigureAwait(false);
                return;
            }

            var record = new MoveRecord(m.AzimuthErrorArcMin, m.AltitudeErrorArcMin, azMove, altMove);
            CancellationTokenSource cts;
            lock (gate) {
                moveCts?.Dispose();
                moveCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                cts = moveCts;
            }

            using (StartKeepAlive(grant.WindowId)) {
                try {
                    SetStatus($"Moving AZ {F(azMove?.MountArcMin ?? 0)}' ALT {F(altMove?.MountArcMin ?? 0)}'" +
                              (azMove?.PreloadReturnArcMin > 0 || altMove?.PreloadReturnArcMin > 0 ? " (with preload)" : ""));
                    await ExecuteAsync(azMove, altMove, settings, cts.Token).ConfigureAwait(false);
                    Azimuth.Commit(azMove);
                    Altitude.Commit(altMove);
                    lock (gate) { lastMove = record; }
                } catch (OperationCanceledException) {
                    await StopAfterInterruptAsync().ConfigureAwait(false);
                    bool wasPaused, wasStopping;
                    lock (gate) { wasPaused = paused; wasStopping = stopping; }
                    if (wasPaused && !wasStopping) {
                        // Answer the measurement we were holding; TPPA applies the settle after the resume.
                        await AnswerMeasurementAsync(grant.WindowId, BridgeReason.Paused, settings.SettleSeconds).ConfigureAwait(false);
                    }
                    return;
                } catch (TimeoutException ex) {
                    await StopAfterInterruptAsync().ConfigureAwait(false);
                    await FaultAsync(BridgeReason.ControllerFault, ex.Message).ConfigureAwait(false);
                    return;
                } catch (Exception ex) {
                    await StopAfterInterruptAsync().ConfigureAwait(false);
                    var reason = mount.IsConnected ? BridgeReason.ControllerFault : BridgeReason.FirmwareDisconnected;
                    await FaultAsync(reason, $"Move failed: {ex.Message}").ConfigureAwait(false);
                    return;
                }
            }

            SetStatus("Settling, waiting for the next measurement");
            await AnswerMeasurementAsync(grant.WindowId, BridgeReason.StepFinished, settings.SettleSeconds, stationary: true).ConfigureAwait(false);
        }

        private void LearnFromLastMove(BridgeMeasurementPayload m, ControllerSettings settings) {
            MoveRecord record;
            lock (gate) {
                record = lastMove;
                lastMove = null;
            }
            if (record == null) { return; }

            LearnAxis(Azimuth, record.Azimuth, record.AzimuthErrorBefore, m.AzimuthErrorArcMin, settings);
            LearnAxis(Altitude, record.Altitude, record.AltitudeErrorBefore, m.AltitudeErrorArcMin, settings);
            log.Info($"[OAT] Calibration: {DescribeCalibration()}");
            PersistCalibration();
        }

        private void LearnAxis(AxisCalibrator axis, AxisMove move, double before, double after, ControllerSettings settings) {
            if (move == null) { return; }
            var outcome = axis.Learn(move, before, after, settings, out var note);
            log.Info($"[OAT] {axis.Axis}: {outcome} - {note}");
            if (outcome == LearnOutcome.DirectionReversed) {
                var inverted = axis.Axis == Axis.Azimuth ? settings.InvertAzimuth : settings.InvertAltitude;
                store.SaveDirectionInverted(axis.Axis, !inverted);
                log.Warning($"[OAT] {axis.Axis} direction looked reversed; it is now {(inverted ? "normal" : "inverted")} (saved to the options).");
            }
        }

        /// <summary>
        /// Firmware sign that reduces a positive error. TPPA: positive azimuth error = move left, positive
        /// altitude error = move down in the north and up in the south. The OAT convention (as used by
        /// OATControl's TPPA log reader) is that <c>:MAZ</c>/<c>:MAL</c> with the error's sign corrects it in
        /// the north; the invert options (or direction detection) fix a mount wired the other way.
        /// </summary>
        public static int DirectionSign(Axis axis, bool northern, ControllerSettings settings) {
            if (axis == Axis.Azimuth) {
                return settings.InvertAzimuth ? -1 : 1;
            }
            var hemisphere = northern ? 1 : -1;
            return hemisphere * (settings.InvertAltitude ? -1 : 1);
        }

        private async Task<BridgeAdjustmentGrantPayload> RequestWindowAsync(BridgeMeasurementPayload m, AxisMove az, AxisMove alt, ControllerSettings settings) {
            var waiter = new TaskCompletionSource<BridgeAdjustmentGrantPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) {
                pendingGrant = waiter;
                pendingGrantMeasurementId = m.MeasurementId;
            }
            try {
                await PublishAsync(BridgeKind.BeginAdjustment, new BridgeAdjustmentRequestPayload {
                    MeasurementId = m.MeasurementId,
                    PlannedAzimuthArcMin = az?.MountArcMin,
                    PlannedAltitudeArcMin = alt?.MountArcMin,
                    DetectingDirection = false,
                    Note = DescribeCalibration()
                }).ConfigureAwait(false);

                var winner = await Task.WhenAny(waiter.Task, Task.Delay(settings.GrantTimeoutMs, lifetime.Token)).ConfigureAwait(false);
                return winner == waiter.Task ? waiter.Task.Result : null;
            } catch (OperationCanceledException) {
                return null;
            } finally {
                lock (gate) {
                    if (ReferenceEquals(pendingGrant, waiter)) {
                        pendingGrant = null;
                        pendingGrantMeasurementId = null;
                    }
                }
            }
        }

        private async Task ExecuteAsync(AxisMove az, AxisMove alt, ControllerSettings settings, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var deadline = DateTime.UtcNow.AddSeconds(settings.MoveTimeoutSeconds);

            // Leg 1: the net move, overshooting by the preload on a negative move. Both axes run together.
            if (az != null) { await mount.MoveAzimuthAsync(az.FirstLegArcMin, token).ConfigureAwait(false); }
            if (alt != null) { await mount.MoveAltitudeAsync(alt.FirstLegArcMin, token).ConfigureAwait(false); }
            await WaitForIdleAsync(settings, deadline, token).ConfigureAwait(false);

            // Leg 2 (preload only): come back positive so the gears finish on the preloaded side.
            var azReturn = az?.PreloadReturnArcMin ?? 0;
            var altReturn = alt?.PreloadReturnArcMin ?? 0;
            if (azReturn <= 0 && altReturn <= 0) { return; }
            if (azReturn > 0) { await mount.MoveAzimuthAsync(azReturn, token).ConfigureAwait(false); }
            if (altReturn > 0) { await mount.MoveAltitudeAsync(altReturn, token).ConfigureAwait(false); }
            await WaitForIdleAsync(settings, deadline, token).ConfigureAwait(false);
        }

        private async Task WaitForIdleAsync(ControllerSettings settings, DateTime deadline, CancellationToken token) {
            var failedReads = 0;
            while (true) {
                await Task.Delay(settings.MotionPollIntervalMs, token).ConfigureAwait(false);
                var motion = await mount.GetMotionAsync(token).ConfigureAwait(false);
                if (motion == null) {
                    if (!mount.IsConnected || ++failedReads >= 8) {
                        throw new IOException("Lost the mount status while moving.");
                    }
                    continue;
                }
                failedReads = 0;
                if (!motion.Value.AnyRunning) { return; }
                if (DateTime.UtcNow > deadline) {
                    throw new TimeoutException($"AZ/ALT motors still running after {F(settings.MoveTimeoutSeconds)} s.");
                }
            }
        }

        private async Task StopAfterInterruptAsync() {
            // The gears stopped somewhere in the middle: neither the direction state nor this move can be trusted.
            Azimuth.ForgetLastDirection();
            Altitude.ForgetLastDirection();
            try {
                if (mount.IsConnected) { await mount.StopAzAltAsync().ConfigureAwait(false); }
            } catch (Exception ex) {
                log.Warning($"[OAT] Could not stop the AZ/ALT motors: {ex.Message}");
            }
        }

        private IDisposable StartKeepAlive(string windowId) {
            var interval = keepAliveMs;
            return new Timer(_ => PublishFireAndForget(BridgeKind.KeepAlive, new BridgeKeepAlivePayload { WindowId = windowId, State = "Moving" }),
                             null, interval, interval);
        }

        private Task AnswerMeasurementAsync(string windowId, string reason, double settleSeconds, bool stationary = true) {
            return PublishAsync(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload {
                WindowId = windowId,
                StationaryAndSettled = stationary,
                SettleSeconds = settleSeconds > 0 ? settleSeconds : null,
                Reason = reason
            });
        }

        private async Task FaultAsync(string reason, string detail) {
            lock (gate) {
                if (faulted) { return; }
                faulted = true;
            }
            log.Error($"[OAT] {reason}: {detail}");
            SetStatus($"Fault: {detail}");
            await PublishAsync(BridgeKind.Fault, new BridgeFaultPayload {
                Reason = reason,
                Detail = detail,
                HardwareStopStatus = BridgeHardwareStopStatus.Ok
            }).ConfigureAwait(false);
        }

        private void CancelMove() {
            CancellationTokenSource cts;
            lock (gate) { cts = moveCts; }
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ------------------------------------------------------------------ timers

        private void OnAnnounceTick() {
            if (disposed) { return; }
            var enabled = Settings().Enabled;
            if (enabled) {
                announcing = true;
                PublishFireAndForget(BridgeKind.Capabilities, new BridgeCapabilitiesAnnouncePayload {
                    Controller = ControllerName,
                    ControllerVersion = ControllerVersion,
                    InterfaceVersion = BridgeContract.InterfaceVersion,
                    SupportedKinds = BridgeKind.Supported,
                    SessionId = SessionId
                });
            } else if (announcing) {
                announcing = false;
                log.Info("[OAT] Disabled in the options: leaving TPPA's system list.");
                PublishFireAndForget(BridgeKind.Cancel, new BridgeCancelPayload { Reason = BridgeReason.BrokerDisabled, Note = "Disabled in the plugin options" });
            }
        }

        private async Task OnReadinessTickAsync() {
            if (disposed) { return; }
            bool active, wasHandedOver, isFaulted;
            lock (gate) {
                active = sessionId != null;
                wasHandedOver = handedOver;
                isFaulted = faulted;
            }
            if (!active) { return; }

            if (wasHandedOver && !isFaulted && !mount.IsConnected) {
                await FaultAsync(BridgeReason.FirmwareDisconnected, "The mount disconnected during the alignment.").ConfigureAwait(false);
                return;
            }
            await ReportReadinessAsync(force: false).ConfigureAwait(false);
        }

        private async Task ReportReadinessAsync(bool force) {
            if (Interlocked.Exchange(ref readinessBusy, 1) == 1 && !force) { return; }
            try {
                var connected = mount.IsConnected;
                var ready = false;
                if (connected) {
                    var motion = await mount.GetMotionAsync(lifetime.Token).ConfigureAwait(false);
                    ready = motion != null && !motion.Value.AnyRunning;
                }

                bool changed;
                lock (gate) {
                    if (sessionId == null) { return; }
                    changed = reportedConnected != connected || reportedReady != ready;
                    reportedConnected = connected;
                    reportedReady = ready;
                }
                if (force || changed) {
                    await PublishAsync(BridgeKind.ControllerReady, new BridgeControllerReadyPayload {
                        Controller = ControllerName,
                        ControllerVersion = ControllerVersion,
                        HardwareConnected = connected,
                        HardwareReady = ready,
                        LinkPath = "ASCOM telescope (N.I.N.A.)",
                        Note = connected ? (ready ? "AZ/ALT idle" : "AZ/ALT moving") : "Mount not connected"
                    }).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                log.Warning($"[OAT] Readiness check failed: {ex.Message}");
            } finally {
                Interlocked.Exchange(ref readinessBusy, 0);
            }
        }

        // ------------------------------------------------------------------ publishing

        private Task PublishAsync(string kind, object payload) {
            var envelope = BridgeEnvelope.Create(kind, SessionId, Interlocked.Increment(ref sequence), BridgeContract.TppaRecipient, payload);
            try {
                return transport.PublishAsync(envelope);
            } catch (Exception ex) {
                log.Error($"[OAT] Publishing '{kind}' failed: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private void PublishFireAndForget(string kind, object payload) {
            _ = PublishAsync(kind, payload).ContinueWith(t => log.Error($"[OAT] Publishing '{kind}' failed: {t.Exception?.GetBaseException().Message}"),
                                                         TaskContinuationOptions.OnlyOnFaulted);
        }

        private ControllerSettings Settings() => settingsSource() ?? new ControllerSettings();

        private void SetStatus(string status) {
            Status = status;
            try { StatusChanged?.Invoke(status); } catch { }
        }

        private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        private sealed class MoveRecord {
            public MoveRecord(double azBefore, double altBefore, AxisMove az, AxisMove alt) {
                AzimuthErrorBefore = azBefore;
                AltitudeErrorBefore = altBefore;
                Azimuth = az;
                Altitude = alt;
            }

            public double AzimuthErrorBefore { get; }
            public double AltitudeErrorBefore { get; }
            public AxisMove Azimuth { get; }
            public AxisMove Altitude { get; }
        }
    }
}
