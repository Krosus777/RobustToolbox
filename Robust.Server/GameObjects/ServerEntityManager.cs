using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Prometheus;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
#if EXCEPTION_TOLERANCE
using Robust.Shared.Exceptions;
#endif
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Prototypes;
using Robust.Shared.Replays;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameObjects
{
    /// <summary>
    /// Manager for entities -- controls things like template loading and instantiation
    /// </summary>
    [UsedImplicitly] // DI Container
    public sealed class ServerEntityManager : EntityManager, IServerEntityManagerInternal
    {
        private static readonly Gauge EntitiesCount = Metrics.CreateGauge(
            "robust_entities_count",
            "Amount of alive entities.");

        [Dependency] private readonly IReplayRecordingManager _replay = default!;
        [Dependency] private readonly IServerNetManager _networkManager = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;
#if EXCEPTION_TOLERANCE
        [Dependency] private readonly IRuntimeLog _runtimeLog = default!;
#endif

        private ISawmill _netEntSawmill = default!;

        public override void Initialize()
        {
            _netEntSawmill = LogManager.GetSawmill("net.ent");

            SetupNetworking();
            ReceivedSystemMessage += (_, systemMsg) => EventBus.RaiseEvent(EventSource.Network, systemMsg);

            base.Initialize();
        }

        EntityUid IServerEntityManagerInternal.AllocEntity(EntityPrototype? prototype)
        {
            return AllocEntity(prototype, out _);
        }

        void IServerEntityManagerInternal.FinishEntityLoad(EntityUid entity, IEntityLoadContext? context)
        {
            LoadEntity(entity, context);
        }

        void IServerEntityManagerInternal.FinishEntityLoad(EntityUid entity, EntityPrototype? prototype, IEntityLoadContext? context)
        {
            LoadEntity(entity, context, prototype);
        }

        void IServerEntityManagerInternal.FinishEntityInitialization(EntityUid entity, MetaDataComponent? meta)
        {
            InitializeEntity(entity, meta);
        }

        void IServerEntityManagerInternal.FinishEntityStartup(EntityUid entity)
        {
            StartEntity(entity);
        }

        private protected override EntityUid CreateEntity(string? prototypeName, IEntityLoadContext? context = null)
        {
            if (prototypeName == null)
                return base.CreateEntity(prototypeName, context);

            if (!PrototypeManager.TryIndex<EntityPrototype>(prototypeName, out var prototype))
                throw new EntityCreationException($"Attempted to spawn an entity with an invalid prototype: {prototypeName}");

            var entity = base.CreateEntity(prototype, context);

            // At this point in time, all data configure on the entity *should* be purely from the prototype.
            // As such, we can reset the modified ticks to Zero,
            // which indicates "not different from client's own deserialization".
            // So the initial data for the component or even the creation doesn't have to be sent over the wire.
            ClearTicks(entity, prototype);
            return entity;
        }

        private void ClearTicks(EntityUid entity, EntityPrototype prototype)
        {
            foreach (var (netId, component) in GetNetComponents(entity))
            {
                // Make sure to ONLY get components that are defined in the prototype.
                // Others could be instantiated directly by AddComponent (e.g. ContainerManager).
                // And those aren't guaranteed to exist on the client, so don't clear them.
                var compName = ComponentFactory.GetComponentName(netId);
                if (prototype.Components.ContainsKey(compName))
                    component.ClearTicks();
            }
        }

        [return: NotNullIfNotNull("uid")]
        public override EntityStringRepresentation? ToPrettyString(EntityUid? uid)
        {
            if (uid == null)
                return null;

            TryGetComponent(uid, out ActorComponent? actor);

            return base.ToPrettyString(uid).Value with { Session = actor?.PlayerSession };
        }

        #region IEntityNetworkManager impl

        public override IEntityNetworkManager EntityNetManager => this;

        /// <inheritdoc />
        public event EventHandler<object>? ReceivedSystemMessage;

        private readonly PriorityQueue<MsgEntity> _queue = new(new MessageSequenceComparer());

        private readonly Dictionary<IPlayerSession, uint> _lastProcessedSequencesCmd =
            new();

        private bool _logLateMsgs;

        /// <inheritdoc />
        public void SetupNetworking()
        {
            _networkManager.RegisterNetMessage<MsgEntity>(HandleEntityNetworkMessage);

            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

            _configurationManager.OnValueChanged(CVars.NetLogLateMsg, b => _logLateMsgs = b, true);
        }

        /// <inheritdoc />
        public override void TickUpdate(float frameTime, bool noPredictions, Histogram? histogram)
        {
            using (histogram?.WithLabels("EntityNet").NewTimer())
            {
                while (_queue.Count != 0 && _queue.Peek().SourceTick <= _gameTiming.CurTick)
                {
                    DispatchEntityNetworkMessage(_queue.Take());
                }
            }

            base.TickUpdate(frameTime, noPredictions, histogram);

            EntitiesCount.Set(Entities.Count);
        }

        public uint GetLastMessageSequence(IPlayerSession session)
        {
            return _lastProcessedSequencesCmd[session];
        }

        /// <inheritdoc />
        public void SendSystemNetworkMessage(EntityEventArgs message, bool recordReplay = true)
        {
            var newMsg = new MsgEntity();
            newMsg.Type = EntityMessageType.SystemMessage;
            newMsg.SystemMessage = message;
            newMsg.SourceTick = _gameTiming.CurTick;

            if (recordReplay)
                _replay.RecordServerMessage(message);

            _networkManager.ServerSendToAll(newMsg);
        }

        /// <inheritdoc />
        public void SendSystemNetworkMessage(EntityEventArgs message, INetChannel targetConnection)
        {
            var newMsg = new MsgEntity();
            newMsg.Type = EntityMessageType.SystemMessage;
            newMsg.SystemMessage = message;
            newMsg.SourceTick = _gameTiming.CurTick;

            _networkManager.ServerSendMessage(newMsg, targetConnection);
        }

        private void HandleEntityNetworkMessage(MsgEntity message)
        {
            var msgT = message.SourceTick;
            var cT = _gameTiming.CurTick;

            if (msgT <= cT)
            {
                if (msgT < cT && _logLateMsgs)
                {
                    _netEntSawmill.Warning("Got late MsgEntity! Diff: {0}, msgT: {2}, cT: {3}, player: {1}",
                        (int) msgT.Value - (int) cT.Value, message.MsgChannel.UserName, msgT, cT);
                }

                DispatchEntityNetworkMessage(message);
                return;
            }

            _queue.Add(message);
        }

        private void DispatchEntityNetworkMessage(MsgEntity message)
        {
            // Don't try to retrieve the session if the client disconnected
            if (!message.MsgChannel.IsConnected)
            {
                return;
            }

            var player = _playerManager.GetSessionByChannel(message.MsgChannel);

            if (message.Sequence != 0)
            {
                if (_lastProcessedSequencesCmd[player] < message.Sequence)
                {
                    _lastProcessedSequencesCmd[player] = message.Sequence;
                }
            }

#if EXCEPTION_TOLERANCE
            try
#endif
            {
                switch (message.Type)
                {
                    case EntityMessageType.SystemMessage:
                        var msg = message.SystemMessage;
                        var sessionType = typeof(EntitySessionMessage<>).MakeGenericType(msg.GetType());
                        var sessionMsg =
                            Activator.CreateInstance(sessionType, new EntitySessionEventArgs(player), msg)!;
                        ReceivedSystemMessage?.Invoke(this, msg);
                        ReceivedSystemMessage?.Invoke(this, sessionMsg);
                        return;
                }
            }
#if EXCEPTION_TOLERANCE
            catch (Exception e)
            {
                _runtimeLog.LogException(e, $"{nameof(DispatchEntityNetworkMessage)}({message.Type})");
            }
#endif
        }

        private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
        {
            switch (args.NewStatus)
            {
                case SessionStatus.Connected:
                    _lastProcessedSequencesCmd.Add(args.Session, 0);
                    break;

                case SessionStatus.Disconnected:
                    _lastProcessedSequencesCmd.Remove(args.Session);
                    break;
            }
        }

        internal sealed class MessageSequenceComparer : IComparer<MsgEntity>
        {
            public int Compare(MsgEntity? x, MsgEntity? y)
            {
                DebugTools.AssertNotNull(x);
                DebugTools.AssertNotNull(y);

                var cmp = y!.SourceTick.CompareTo(x!.SourceTick);
                if (cmp != 0)
                {
                    return cmp;
                }

                return y.Sequence.CompareTo(x.Sequence);
            }
        }

        #endregion
    }
}
