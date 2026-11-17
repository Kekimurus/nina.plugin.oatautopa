// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// The names and payload shapes in this file mirror the external correction contract of the
// Three Point Polar Alignment plugin (https://github.com/isbeorn/nina.plugin.polaralignment,
// PolarAlignment/Bridge/BridgeContract.cs, docs/external-correction.md). The two plugins do not
// share an assembly: any change on the TPPA side has to be mirrored here.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OatAutoPA.Core {

    public static class BridgeContract {
        public const int InterfaceVersion = 1;

        /// <summary>Published by TPPA, subscribed by this controller.</summary>
        public const string EventTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalEvent";

        /// <summary>Published by this controller, subscribed by TPPA.</summary>
        public const string CommandTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalCommand";

        /// <summary>Address TPPA answers to.</summary>
        public const string TppaRecipient = "TPPA";
    }

    public static class BridgeKind {
        public const string Capabilities = "Capabilities";
        public const string ControllerReady = "ControllerReady";
        public const string Measurement = "Measurement";
        public const string BeginAdjustment = "BeginAdjustment";
        public const string AdjustmentGranted = "AdjustmentGranted";
        public const string RequestMeasurement = "RequestMeasurement";
        public const string RequestCompletion = "RequestCompletion";
        public const string KeepAlive = "KeepAlive";
        public const string PauseRequested = "PauseRequested";
        public const string SessionState = "SessionState";
        public const string StopRequested = "StopRequested";
        public const string Stopped = "Stopped";
        public const string Cancel = "Cancel";
        public const string Fault = "Fault";
        public const string SessionEnded = "SessionEnded";

        /// <summary>Kinds this controller sends or understands, announced in <c>Capabilities</c>.</summary>
        public static readonly string[] Supported = {
            Capabilities, ControllerReady, Measurement, BeginAdjustment, AdjustmentGranted,
            RequestMeasurement, RequestCompletion, KeepAlive, PauseRequested, SessionState,
            StopRequested, Stopped, Cancel, Fault, SessionEnded
        };
    }

    public static class BridgeReason {
        public const string UserStop = "UserStop";
        public const string Disconnect = "Disconnect";
        public const string FirmwareDisconnected = "FirmwareDisconnected";
        public const string SessionTimeout = "SessionTimeout";
        public const string ControllerFault = "ControllerFault";
        public const string StepFinished = "StepFinished";
        public const string CompletionRequested = "CompletionRequested";
        public const string Paused = "Paused";
        public const string Resumed = "Resumed";
        public const string BrokerDisabled = "BrokerDisabled";

        // Reasons of our own, only used in free-text fields TPPA logs.
        public const string NoMoveNeeded = "NoMoveNeeded";
        public const string Unusable = "Unusable";
        public const string ConfirmTolerance = "ConfirmTolerance";
        public const string NotGranted = "NotGranted";
    }

    public static class BridgeState {
        public const string Preparing = "Preparing";
        public const string Measuring = "Measuring";
        public const string WaitingForRequest = "WaitingForRequest";
        public const string WindowOpen = "WindowOpen";
        public const string Verifying = "Verifying";
        public const string Ended = "Ended";
    }

    public static class BridgeMeasurementStatus {
        public const string Valid = "Valid";
        public const string Unstable = "Unstable";
        public const string CaptureFailed = "CaptureFailed";
    }

    public static class BridgeHardwareStopStatus {
        public const string Ok = "ok";
        public const string Unknown = "unknown";
        public const string Fault = "fault";
    }

    /// <summary>
    /// Wire envelope. Its JSON is the <c>IMessage.Content</c> string on both topics. TPPA uses
    /// Newtonsoft with default settings (PascalCase, nulls written); System.Text.Json with no naming
    /// policy writes the same shape, and case-insensitive reading accepts what TPPA writes.
    /// </summary>
    public sealed class BridgeEnvelope {
        public int Version { get; set; } = BridgeContract.InterfaceVersion;
        public string SessionId { get; set; }
        public string CommandId { get; set; }
        public string ReplyTo { get; set; }
        public long SequenceNumber { get; set; }
        public string Kind { get; set; }
        public string IntendedRecipient { get; set; }
        public JsonElement? Payload { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static BridgeEnvelope FromJson(string json) {
            if (string.IsNullOrWhiteSpace(json)) { return null; }
            try {
                return JsonSerializer.Deserialize<BridgeEnvelope>(json, JsonOptions);
            } catch (JsonException) {
                return null;
            }
        }

        public T PayloadAs<T>() where T : class {
            if (Payload == null || Payload.Value.ValueKind != JsonValueKind.Object) { return null; }
            try {
                return Payload.Value.Deserialize<T>(JsonOptions);
            } catch (JsonException) {
                return null;
            }
        }

        public static BridgeEnvelope Create(string kind, string sessionId, long sequenceNumber,
                                            string recipient, object payload, string replyTo = null) {
            return new BridgeEnvelope {
                Kind = kind,
                SessionId = sessionId,
                CommandId = Guid.NewGuid().ToString("N"),
                ReplyTo = replyTo,
                SequenceNumber = sequenceNumber,
                IntendedRecipient = recipient,
                Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions)
            };
        }
    }
}
