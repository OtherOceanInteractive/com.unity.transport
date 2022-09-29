using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport.Relay;

namespace Unity.Networking.Transport
{
    internal struct RelayLayer : INetworkLayer
    {
        private const int k_DeferredSendQueueSize = 10;

        private struct ProtocolData
        {
            public RelayConnectionStatus ConnectionStatus;
            public RelayServerData ServerData;
            public ConnectionId UnderlyingConnection;
            public long LastSentTime;
            public int ConnectTimeout;
            public int HeartbeatTime;
        }

        private struct ConnectionData
        {
            public long LastConnectAttempt;
        }

        /// <summary>
        /// Connections at this layer level are connections to Relay Allocation Ids.
        /// That is, other clients connected through the Relay server. Therefore, the
        /// endpoints of all these connections are actually Allocation Ids.
        /// </summary>
        private ConnectionList m_Connections;

        /// <summary>
        /// Underlying connections contains only one connection: the one between this
        /// driver and the Relay server, using regular endpoints.
        /// </summary>
        private ConnectionList m_UnderlyingConnections;

        private PacketsQueue m_DeferredSendQueue;
        private NativeReference<ProtocolData> m_ProtocolData;
        private ConnectionDataMap<ConnectionData> m_ConnectionsData;
        private NativeParallelHashMap<NetworkEndpoint, ConnectionId> m_EndpointsHashMap;

        public RelayConnectionStatus ConnectionStatus => m_ProtocolData.Value.ConnectionStatus;

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            var protocolData = new ProtocolData
            {
                ConnectionStatus = RelayConnectionStatus.NotEstablished,
                ConnectTimeout = settings.GetNetworkConfigParameters().connectTimeoutMS,
                HeartbeatTime = settings.GetRelayParameters().RelayConnectionTimeMS,
                ServerData = settings.GetRelayParameters().ServerData,
            };

            m_ProtocolData = new NativeReference<ProtocolData>(protocolData, Allocator.Persistent);
            m_DeferredSendQueue = new PacketsQueue(k_DeferredSendQueueSize);
            m_ConnectionsData = new ConnectionDataMap<ConnectionData>(1, default, Allocator.Persistent);
            m_EndpointsHashMap = new NativeParallelHashMap<NetworkEndpoint, ConnectionId>(1, Allocator.Persistent);

            m_DeferredSendQueue.SetDefaultDataOffset(packetPadding);

            if (connectionList.IsCreated)
                m_UnderlyingConnections = connectionList;

            connectionList = m_Connections = ConnectionList.Create();

            packetPadding += RelayMessageRelay.k_Length;

            return 0;
        }

        public void Dispose()
        {
            m_Connections.Dispose();
            m_ProtocolData.Dispose();
            m_ConnectionsData.Dispose();
            m_EndpointsHashMap.Dispose();
            m_DeferredSendQueue.Dispose();
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            if (m_UnderlyingConnections.IsCreated)
                return ScheduleReceive(new ReceiveJob<UnderlyingConnectionList>(), new UnderlyingConnectionList(ref m_UnderlyingConnections), ref arguments, dependency);
            else
                return ScheduleReceive(new ReceiveJob<NullUnderlyingConnectionList>(), default, ref arguments, dependency);
        }

        private JobHandle ScheduleReceive<T>(ReceiveJob<T> job, T underlyingConnectionList, ref ReceiveJobArguments arguments, JobHandle dependency)
            where T : unmanaged, IUnderlyingConnectionList
        {
            job.Connections = m_Connections;
            job.ConnectionsData = m_ConnectionsData;
            job.EndpointsHashmap = m_EndpointsHashMap;
            job.ReceiveQueue = arguments.ReceiveQueue;
            job.UnderlyingConnections = underlyingConnectionList;
            job.DeferredSendQueue = m_DeferredSendQueue;
            job.RelayProtocolData = m_ProtocolData;
            job.Time = arguments.Time;

            return job.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new SendJob
            {
                Connections = m_Connections,
                SendQueue = arguments.SendQueue,
                DeferredSendQueue = m_DeferredSendQueue,
                RelayProtocolData = m_ProtocolData,
                Time = arguments.Time,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct SendJob : IJob
        {
            public ConnectionList Connections;
            public PacketsQueue DeferredSendQueue;
            public PacketsQueue SendQueue;
            public NativeReference<ProtocolData> RelayProtocolData;
            public long Time;

            public void Execute()
            {
                // Process all data messages.
                var fromAllocationId = RelayProtocolData.Value.ServerData.AllocationId;
                var underlyingConnectionId = RelayProtocolData.Value.UnderlyingConnection;
                var underlyingEndpoint = RelayProtocolData.Value.ServerData.Endpoint;
                var count = SendQueue.Count;
                var actualSend = DeferredSendQueue.Count > 0;

                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = SendQueue[i];

                    if (packetProcessor.Length == 0)
                        continue;

                    var connection = packetProcessor.ConnectionRef;
                    var endpoint = Connections.GetConnectionEndpoint(connection);

                    RelayMessageRelay.Write(ref packetProcessor, ref fromAllocationId, ref endpoint.AsRelayAllocationId(), (ushort)packetProcessor.Length);
                    packetProcessor.ConnectionRef = underlyingConnectionId;
                    packetProcessor.EndpointRef = underlyingEndpoint;

                    actualSend = true;
                }

                // Send all deferred packets.
                SendQueue.EnqueuePackets(ref DeferredSendQueue);
                DeferredSendQueue.Clear();

                if (actualSend)
                {
                    var protocolData = RelayProtocolData.Value;
                    protocolData.LastSentTime = Time;
                    RelayProtocolData.Value = protocolData;
                }
            }
        }

        [BurstCompile]
        private struct ReceiveJob<T> : IJob where T : unmanaged, IUnderlyingConnectionList
        {
            public ConnectionList Connections;
            public ConnectionDataMap<ConnectionData> ConnectionsData;
            public NativeParallelHashMap<NetworkEndpoint, ConnectionId> EndpointsHashmap;
            public NativeReference<ProtocolData> RelayProtocolData;
            public T UnderlyingConnections;
            public PacketsQueue ReceiveQueue;
            public PacketsQueue DeferredSendQueue;
            public long Time;

            public void Execute()
            {
                ProcessReceivedMessages();
                ProcessRelayServerConnection();
                ProcessConnectionStates();
            }

            private void ProcessReceivedMessages()
            {
                var protocolData = RelayProtocolData.Value;
                var count = ReceiveQueue.Count;

                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = ReceiveQueue[i];

                    if (packetProcessor.Length < RelayMessageHeader.k_Length)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    var header = packetProcessor.GetPayloadDataRef<RelayMessageHeader>();

                    if (!header.IsValid())
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    switch (header.Type)
                    {
                        case RelayMessageType.BindReceived:
                        {
                            packetProcessor.Drop();
                            protocolData.ConnectionStatus = RelayConnectionStatus.Established;
                            break;
                        }
                        case RelayMessageType.Accepted:
                        {
                            var acceptedMessage = packetProcessor.GetPayloadDataRef<RelayMessageAccepted>();

                            // An Accepted message can be received only when we requested the connection,
                            // and as we can only connect to the host, only one connection should be present in the list.
                            if (Connections.Count == 1)
                            {
                                var connectionId = Connections.ConnectionAt(0);

                                if (Connections.GetConnectionState(connectionId) == NetworkConnection.State.Connecting)
                                {
                                    var newEndpoint = acceptedMessage.FromAllocationId.ToNetworkEndpoint();
                                    Connections.FinishConnectingFromLocal(ref connectionId);
                                    Connections.UpdateConnectionAddress(ref connectionId, ref newEndpoint);
                                    EndpointsHashmap.Add(newEndpoint, connectionId);
                                }
                            }
                            else
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                UnityEngine.Debug.LogError("Received a Relay Accepted message but there is not one connection in the list." +
                                    "One and only one connection is expected when initiating a connection using Relay");
#endif
                            }

                            packetProcessor.Drop();
                            break;
                        }
                        case RelayMessageType.Disconnect:
                        {
                            var disconnectMessage = packetProcessor.GetPayloadDataRef<RelayMessageDisconnect>();

                            // else, we received a disconnect request that was not for us, that should not happen.
                            if (disconnectMessage.ToAllocationId == RelayProtocolData.Value.ServerData.AllocationId)
                            {
                                var endpoint = disconnectMessage.FromAllocationId.ToNetworkEndpoint();
                                if (EndpointsHashmap.TryGetValue(endpoint, out var connectionId))
                                {
                                    Connections.StartDisconnecting(ref connectionId);
                                    Connections.FinishDisconnecting(ref connectionId, Error.DisconnectReason.ClosedByRemote);
                                    EndpointsHashmap.Remove(endpoint);
                                }
                            }

                            packetProcessor.Drop();
                            break;
                        }
                        case RelayMessageType.Relay:
                        {
                            var relayHeader = packetProcessor.RemoveFromPayloadStart<RelayMessageRelay>();
                            if (relayHeader.DataLength != packetProcessor.Length)
                            {
                                packetProcessor.Drop();
                            }
                            else
                            {
                                var destination = relayHeader.ToAllocationId;
                                if (destination != RelayProtocolData.Value.ServerData.AllocationId)
                                {
                                    packetProcessor.Drop();
                                }
                                else
                                {
                                    var endpoint = relayHeader.FromAllocationId.ToNetworkEndpoint();
                                    if (!EndpointsHashmap.TryGetValue(endpoint, out var connectionId))
                                    {
                                        // Relay does not notify of new connections, so we can only know about them
                                        // when receiving a relay message from a new allocation id.
                                        connectionId = Connections.StartConnecting(ref endpoint);
                                        Connections.FinishConnectingFromRemote(ref connectionId);
                                        EndpointsHashmap.TryAdd(endpoint, connectionId);
                                    }
                                    packetProcessor.EndpointRef = endpoint;
                                    packetProcessor.ConnectionRef = connectionId;
                                }
                            }
                            break;
                        }
                        case RelayMessageType.Error:
                        {
                            var errorMessage = packetProcessor.GetPayloadDataRef<RelayMessageError>();

                            errorMessage.LogError();

                            // ClientPlayerMismatch error means our IP has change and we need to rebind.
                            if (errorMessage.ErrorCode == 3)
                            {
                                protocolData.ServerData.Nonce++;
                                // Send a (re)Bind message
                                if (DeferredSendQueue.EnqueuePacket(out var bindPacket))
                                {
                                    RelayMessageBind.Write(ref bindPacket, ref protocolData.ServerData);
                                    bindPacket.ConnectionRef = protocolData.UnderlyingConnection;
                                    bindPacket.EndpointRef = protocolData.ServerData.Endpoint;
                                }
                            }
                            // Allocation time outs and failure to find the allocation indicate that the allocation
                            // is not valid anymore, and that users will need to recreate a new one.
                            else if (errorMessage.ErrorCode == 1 || errorMessage.ErrorCode == 4)
                            {
                                protocolData.ConnectionStatus = RelayConnectionStatus.AllocationInvalid;
                            }

                            packetProcessor.Drop();
                            break;
                        }
                        default:
                            packetProcessor.Drop();
                            break;
                    }
                }

                RelayProtocolData.Value = protocolData;
            }

            private void ProcessRelayServerConnection()
            {
                var protocolData = RelayProtocolData.Value;

                switch (protocolData.ConnectionStatus)
                {
                    case RelayConnectionStatus.NotEstablished:
                    {
                        if (UnderlyingConnections.TryConnect(ref protocolData.ServerData.Endpoint, ref protocolData.UnderlyingConnection))
                        {
                            // When not connection is established LastSentTime is the last time we tried to bind.
                            if (Time - protocolData.LastSentTime > protocolData.ConnectTimeout ||
                                protocolData.LastSentTime == 0)
                            {
                                protocolData.LastSentTime = Time;

                                // Send a Bind message
                                if (DeferredSendQueue.EnqueuePacket(out var packetProcessor))
                                {
                                    RelayMessageBind.Write(ref packetProcessor, ref protocolData.ServerData);
                                    packetProcessor.ConnectionRef = protocolData.UnderlyingConnection;
                                    packetProcessor.EndpointRef = protocolData.ServerData.Endpoint;
                                }
                            }
                        }
                        break;
                    }
                    case RelayConnectionStatus.Established:
                    {
                        if (Time - protocolData.LastSentTime >= protocolData.HeartbeatTime)
                        {
                            // Send a Ping message
                            if (DeferredSendQueue.EnqueuePacket(out var packetProcessor))
                            {
                                RelayMessagePing.Write(ref packetProcessor, ref protocolData.ServerData.AllocationId);
                                packetProcessor.ConnectionRef = protocolData.UnderlyingConnection;
                                packetProcessor.EndpointRef = protocolData.ServerData.Endpoint;
                            }
                        }
                        break;
                    }
                }

                RelayProtocolData.Value = protocolData;
            }

            private void ProcessConnectionStates()
            {
                var count = Connections.Count;
                for (int i = 0; i < count; i++)
                {
                    var connectionId = Connections.ConnectionAt(i);
                    var connectionState = Connections.GetConnectionState(connectionId);

                    switch (connectionState)
                    {
                        case NetworkConnection.State.Disconnecting:
                            ProcessDisconnecting(ref connectionId);
                            break;
                        case NetworkConnection.State.Connecting:
                            ProcessConnecting(ref connectionId);
                            break;
                    }
                }
            }

            private void ProcessDisconnecting(ref ConnectionId connectionId)
            {
                var connectionData = ConnectionsData[connectionId];
                var protocolData = RelayProtocolData.Value;

                if (protocolData.ConnectionStatus == RelayConnectionStatus.Established)
                {
                    // Send a Disconnect message
                    if (DeferredSendQueue.EnqueuePacket(out var packetProcessor))
                    {
                        var endpoint = Connections.GetConnectionEndpoint(connectionId);
                        RelayMessageDisconnect.Write(ref packetProcessor,
                            ref protocolData.ServerData.AllocationId,
                            ref endpoint.AsRelayAllocationId());

                        packetProcessor.ConnectionRef = protocolData.UnderlyingConnection;
                        packetProcessor.EndpointRef = protocolData.ServerData.Endpoint;
                    }
                }

                Connections.FinishDisconnecting(ref connectionId);

                ConnectionsData.ClearData(ref connectionId);
                EndpointsHashmap.Remove(Connections.GetConnectionEndpoint(connectionId));
            }

            private void ProcessConnecting(ref ConnectionId connectionId)
            {
                if (Connections.Count > 1)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Connect can only be called once when using Relay");
#endif
                    Connections.StartDisconnecting(ref connectionId);
                    Connections.FinishDisconnecting(ref connectionId);
                    return;
                }

                var connectionData = ConnectionsData[connectionId];

                if (Time - connectionData.LastConnectAttempt >= RelayProtocolData.Value.ConnectTimeout)
                {
                    var protocolData = RelayProtocolData.Value;

                    connectionData.LastConnectAttempt = Time;
                    ConnectionsData[connectionId] = connectionData;

                    // Send a ConnectRequest message
                    if (DeferredSendQueue.EnqueuePacket(out var packetProcessor))
                    {
                        RelayMessageConnectRequest.Write(ref packetProcessor,
                            ref protocolData.ServerData.AllocationId,
                            ref protocolData.ServerData.HostConnectionData);

                        packetProcessor.ConnectionRef = protocolData.UnderlyingConnection;
                        packetProcessor.EndpointRef = protocolData.ServerData.Endpoint;
                    }
                }
            }
        }
    }
}