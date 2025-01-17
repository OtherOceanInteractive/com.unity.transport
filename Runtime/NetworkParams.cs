using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Interface used to implement custom values for use with <see cref="NetworkSettings"/>. This
    /// is only useful to provide configuration settings for custom <see cref="INetworkInterface"/>
    /// or <see cref="INetworkPipelineStage"/> implementations.
    /// </summary>
    public interface INetworkParameter
    {
        /// <summary>
        /// Checks if the values for all fields are valid. This method will be automatically called
        /// when adding parameters to the <see cref="NetworkSettings"/>.
        /// </summary>
        /// <returns>True if the parameter is valid, false otherwise.</returns>
        bool Validate();
    }

    /// <summary>
    /// Default values used for the different network parameters. These parameters (except the MTU)
    /// can be set with <see cref="CommonNetworkParametersExtensions.WithNetworkConfigParameters"/>.
    /// </summary>
    public struct NetworkParameterConstants
    {
        /// <summary>Default time between connection attempts.</summary>
        /// <value>Timeout in milliseconds.</value>
        public const int ConnectTimeoutMS = 1000;

        /// <summary>
        /// Default maximum number of connection attempts to try. If no answer is received from the
        /// server after this number of attempts, a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// is generated for the connection.
        /// </summary>
        /// <value>Number of attempts.</value>
        public const int MaxConnectAttempts = 60;

        /// <summary>
        /// Default inactivity timeout for a connection. If nothing is received on a connection for
        /// this amount of time, it is disconnected (a <see cref="NetworkEvent.Type.Disconnect"/>
        /// event will be generated).
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public const int DisconnectTimeoutMS = 30 * 1000;

        /// <summary>
        /// Default amount of time after which if nothing from a peer is received, a heartbeat
        /// message will be sent to keep the connection alive.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public const int HeartbeatTimeoutMS = 500;

        /// <summary>
        /// Default amount of time after which to attempt to re-establish a connection if nothing is
        /// received from the peer. This is used to re-establish connections for example when a
        /// peer's IP address changes (e.g. mobile roaming scenarios).
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public const int ReconnectionTimeoutMS = 2000;

        /// <summary>Default capacity of the receive queue.</summary>
        /// <value>Queue capacity in number of packets.</value>
        public const int ReceiveQueueCapacity = 512;

        /// <summary>Default capacity of the send queue.</summary>
        /// <value>Queue capacity in number of packets.</value>
        public const int SendQueueCapacity = 512;

        /// <summary>
        /// Maximum size of a packet that can be sent by the transport. Note that this size includes
        /// any headers that could be added by the transport (e.g. headers for DTLS or pipelines),
        /// which means the actual maximum message size that can be sent by a user is slightly less
        /// than this value. To find out what the size of these headers is, use
        /// <see cref="NetworkDriver.MaxHeaderSize"/>. It is possible to send messages larger than
        /// that by sending them through a pipeline with a <see cref="FragmentationPipelineStage"/>.
        /// </summary>
        /// <value>Size in bytes.</value>
        public const int MTU = 1400;

        internal const int InitialEventQueueSize = 100;
    }

    /// <summary>
    /// Configuration structure for a <see cref="NetworkDriver"/>, containing general parameters.
    /// </summary>
    public struct NetworkConfigParameter : INetworkParameter
    {
        /// <summary>Time between connection attempts.</summary>
        /// <value>Timeout in milliseconds.</value>
        public int connectTimeoutMS;

        /// <summary>
        /// Maximum number of connection attempts to try. If no answer is received from the server
        /// after this number of attempts, a <see cref="NetworkEvent.Type.Disconnect"/> event is
        /// generated for the connection.
        /// </summary>
        /// <value>Number of attempts.</value>
        public int maxConnectAttempts;

        /// <summary>
        /// Inactivity timeout for a connection. If nothing is received on a connection for this
        /// amount of time, it is disconnected (a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// will be generated). To prevent this from happenning when the game session is simply
        /// quiet, set <see cref="heartbeatTimeoutMS"/> to a positive non-zero value.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int disconnectTimeoutMS;

        /// <summary>
        /// Time after which if nothing from a peer is received, a heartbeat message will be sent
        /// to keep the connection alive. Prevents the <see cref="disconnectTimeoutMS"/> mechanism
        /// from kicking when nothing happens on a connection. A value of 0 will disable heartbeats.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int heartbeatTimeoutMS;

        /// <summary>
        /// Time after which to attempt to re-establish a connection if nothing is received from the
        /// peer. This is used to re-establish connections for example when a peer's IP address
        /// changes (e.g. mobile roaming scenarios). To be effective, should be less than
        /// <see cref="disconnectTimeoutMS"/> but greater than <see cref="heartbeatTimeoutMS"/>. A
        /// value of 0 will disable this functionality.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int reconnectionTimeoutMS;

        /// <summary>
        /// Maximum amount of time a single frame can advance timeout values. In this scenario, a
        /// frame is defined as the time between two <see cref="NetworkDriver.ScheduleUpdate"/>
        /// calls. Useful when debugging to avoid connection timeouts.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int maxFrameTimeMS;

        /// <summary>
        /// Fixes a fixed amount of time to be used each frame for the purpose of timeout
        /// calculations. For example, setting this value to 1000 will have the driver consider
        /// that 1 second passed between each call to <see cref="NetworkDriver.ScheduleUpdate"/>,
        /// even if that's not actually the case. Only useful for testing scenarios, where
        /// deterministic behavior is more important than correctness.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int fixedFrameTimeMS;

        /// <summary>
        /// Capacity of the receive queue. This should be the maximum number of packets expected to
        /// be received in a single update (each frame). The best value for this will depend heavily
        /// on the game type, but generally should be a multiple of the maximum number of players.
        /// The only impact of increasing this value is increased memory usage, with an expected
        /// ~1400 bytes of memory being used per unit of capacity. The queue is shared across all
        /// connections, so servers should set this higher than clients.
        /// </summary>
        /// <value>Queue capacity in number of packets.</value>
        public int receiveQueueCapacity;

        /// <summary>
        /// Capacity of the send queue. This should be the maximum number of packets expected to be
        /// send in a single update (each frame). The best value will depend heavily on the game
        /// type, but generally should be a multiple of the maximum number of players. The only
        /// impact of increasing this value is increased memory usage, with an expected ~1400 bytes
        /// of memory being used per unity of capacity. The queue is shared across all connections,
        /// so servers should set this higher than clients.
        /// </summary>
        /// <value>Queue capacity in number of packets.</value>
        public int sendQueueCapacity;

        /// <inheritdoc/>
        public bool Validate()
        {
            var valid = true;

            if (connectTimeoutMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("connectTimeoutMS", connectTimeoutMS);
            }
            if (maxConnectAttempts < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("maxConnectAttempts", maxConnectAttempts);
            }
            if (disconnectTimeoutMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("disconnectTimeoutMS", disconnectTimeoutMS);
            }
            if (heartbeatTimeoutMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("heartbeatTimeoutMS", heartbeatTimeoutMS);
            }
            if (reconnectionTimeoutMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("reconnectionTimeoutMS", reconnectionTimeoutMS);
            }
            if (maxFrameTimeMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("maxFrameTimeMS", maxFrameTimeMS);
            }
            if (fixedFrameTimeMS < 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("fixedFrameTimeMS", fixedFrameTimeMS);
            }
            if (receiveQueueCapacity <= 0)
            {
                valid = false;
                DebugLog.ErrorValueIsZeroOrNegative("receiveQueueCapacity", receiveQueueCapacity);
            }
            if (sendQueueCapacity <= 0)
            {
                valid = false;
                DebugLog.ErrorValueIsZeroOrNegative("sendQueueCapacity", sendQueueCapacity);
            }

            return valid;
        }
    }

    /// <summary>Extensions for <see cref="NetworkConfigParameter"/>.</summary>
    public static class CommonNetworkParametersExtensions
    {
        /// <summary>
        /// Sets the <see cref="NetworkConfigParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to modify.</param>
        /// <param name="connectTimeoutMS"><inheritdoc cref="NetworkConfigParameter.connectTimeoutMS" path="/summary"/></param>
        /// <param name="maxConnectAttempts"><inheritdoc cref="NetworkConfigParameter.maxConnectAttempts" path="/summary"/></param>
        /// <param name="disconnectTimeoutMS"><inheritdoc cref="NetworkConfigParameter.disconnectTimeoutMS" path="/summary"/></param>
        /// <param name="heartbeatTimeoutMS"><inheritdoc cref="NetworkConfigParameter.heartbeatTimeoutMS" path="/summary"/></param>
        /// <param name="reconnectionTimeoutMS"><inheritdoc cref="NetworkConfigParameter.reconnectionTimeoutMS" path="/summary"/></param>
        /// <param name="maxFrameTimeMS"><inheritdoc cref="NetworkConfigParameter.maxFrameTimeMS" path="/summary"/></param>
        /// <param name="fixedFrameTimeMS"><inheritdoc cref="NetworkConfigParameter.fixedFrameTimeMS" path="/summary"/></param>
        /// <param name="receiveQueueCapacity"><inheritdoc cref="NetworkConfigParameter.receiveQueueCapacity" path="/summary"/></param>
        /// <param name="sendQueueCapacity"><inheritdoc cref="NetworkConfigParameter.sendQueueCapacity" path="/summary"/></param>
        /// <returns>Settings structure with modified values.</returns>
        public static ref NetworkSettings WithNetworkConfigParameters(
            ref this NetworkSettings settings,
            int connectTimeoutMS        = NetworkParameterConstants.ConnectTimeoutMS,
            int maxConnectAttempts      = NetworkParameterConstants.MaxConnectAttempts,
            int disconnectTimeoutMS     = NetworkParameterConstants.DisconnectTimeoutMS,
            int heartbeatTimeoutMS      = NetworkParameterConstants.HeartbeatTimeoutMS,
            int reconnectionTimeoutMS   = NetworkParameterConstants.ReconnectionTimeoutMS,
            int maxFrameTimeMS          = 0,
            int fixedFrameTimeMS        = 0,
            int receiveQueueCapacity    = NetworkParameterConstants.ReceiveQueueCapacity,
            int sendQueueCapacity       = NetworkParameterConstants.SendQueueCapacity
        )
        {
            var parameter = new NetworkConfigParameter
            {
                connectTimeoutMS = connectTimeoutMS,
                maxConnectAttempts = maxConnectAttempts,
                disconnectTimeoutMS = disconnectTimeoutMS,
                heartbeatTimeoutMS = heartbeatTimeoutMS,
                reconnectionTimeoutMS = reconnectionTimeoutMS,
                maxFrameTimeMS = maxFrameTimeMS,
                fixedFrameTimeMS = fixedFrameTimeMS,
                receiveQueueCapacity = receiveQueueCapacity,
                sendQueueCapacity = sendQueueCapacity,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="NetworkConfigParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to get parameters from.</param>
        /// <returns>Structure containing the network parameters.</returns>
        public static NetworkConfigParameter GetNetworkConfigParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<NetworkConfigParameter>(out var parameters))
            {
                parameters.connectTimeoutMS      = NetworkParameterConstants.ConnectTimeoutMS;
                parameters.maxConnectAttempts    = NetworkParameterConstants.MaxConnectAttempts;
                parameters.disconnectTimeoutMS   = NetworkParameterConstants.DisconnectTimeoutMS;
                parameters.heartbeatTimeoutMS    = NetworkParameterConstants.HeartbeatTimeoutMS;
                parameters.reconnectionTimeoutMS = NetworkParameterConstants.ReconnectionTimeoutMS;
                parameters.receiveQueueCapacity  = NetworkParameterConstants.ReceiveQueueCapacity;
                parameters.sendQueueCapacity     = NetworkParameterConstants.SendQueueCapacity;
                parameters.maxFrameTimeMS        = 0;
                parameters.fixedFrameTimeMS      = 0;
            }

            return parameters;
        }
    }
}
