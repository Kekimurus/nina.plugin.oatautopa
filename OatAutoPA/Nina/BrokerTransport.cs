// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using NINA.Plugin.Interfaces;
using OatAutoPA.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OatAutoPA.Nina {

    /// <summary>Connects the controller to N.I.N.A.'s in-process message broker.</summary>
    public sealed class BrokerTransport : IBridgeTransport, ISubscriber, IDisposable {
        private readonly IMessageBroker broker;
        private readonly Guid senderId;
        private readonly string senderName;
        private bool disposed;

        public BrokerTransport(IMessageBroker broker, Guid senderId, string senderName) {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            this.senderId = senderId;
            this.senderName = senderName;
            broker.Subscribe(BridgeContract.EventTopic, this);
        }

        public event Action<BridgeEnvelope> Received;

        public Task PublishAsync(BridgeEnvelope envelope) {
            if (disposed) { return Task.CompletedTask; }
            return broker.Publish(new CommandMessage(envelope.ToJson(), senderId, senderName));
        }

        public Task OnMessageReceived(IMessage message) {
            // The broker awaits this call: hand over and return at once.
            if (disposed || message == null || message.Topic != BridgeContract.EventTopic) { return Task.CompletedTask; }
            var envelope = BridgeEnvelope.FromJson(message.Content as string);
            if (envelope != null) { Received?.Invoke(envelope); }
            return Task.CompletedTask;
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            broker.Unsubscribe(BridgeContract.EventTopic, this);
        }

        /// <summary>
        /// Message on TPPA's command topic. <see cref="SenderId"/> is this plugin's GUID: TPPA identifies (and
        /// binds a run to) a controller by it, so it must be stable and unique.
        /// </summary>
        private sealed class CommandMessage : IMessage {
            public CommandMessage(string json, Guid senderId, string sender) {
                Content = json;
                SenderId = senderId;
                Sender = sender;
            }

            public Guid SenderId { get; }
            public string Sender { get; }
            public DateTimeOffset SentAt { get; } = DateTimeOffset.UtcNow;
            public Guid MessageId { get; } = Guid.NewGuid();
            public DateTimeOffset? Expiration => null;
            public Guid? CorrelationId => null;
            public int Version => BridgeContract.InterfaceVersion;
            public IDictionary<string, object> CustomHeaders { get; } = new Dictionary<string, object>();
            public string Topic => BridgeContract.CommandTopic;
            public object Content { get; }
        }
    }
}
