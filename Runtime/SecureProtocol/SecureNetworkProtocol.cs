#if ENABLE_MANAGED_UNITYTLS

using System;
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;

using Unity.TLS.LowLevel;
using UnityEngine;
using size_t = System.UIntPtr;

namespace Unity.Networking.Transport.TLS
{
    internal struct SecureClientState
    {
        public unsafe Binding.unitytls_client* ClientPtr;
        public unsafe Binding.unitytls_client_config* ClientConfig;
    }

    struct SecureNetworkProtocolData
    {
        public UnsafeHashMap<NetworkInterfaceEndPoint, SecureClientState> SecureClients;
        public FixedString4096Bytes  Pem;
        public FixedString4096Bytes  Rsa;
        public FixedString4096Bytes  RsaKey;
        public FixedString32Bytes    Hostname;
        public uint             Protocol;
        public uint             SSLReadTimeoutMs;
        public uint             SSLHandshakeTimeoutMax;
        public uint             SSLHandshakeTimeoutMin;
        public uint             ClientAuth;
    }

    internal struct SecureUserData
    {
        public IntPtr StreamData;
        public NetworkSendInterface Interface;
        public NetworkInterfaceEndPoint Remote;
        public NetworkSendQueueHandle QueueHandle;
        public int Size;
        public int BytesProcessed;
    }

    internal static class ManagedSecureFunctions
    {
        private const int UNITYTLS_ERR_SSL_WANT_READ = -0x6900;
        private const int UNITYTLS_ERR_SSL_WANT_WRITE = -0x6880;

        private static Binding.unitytls_client_data_send_callback s_sendCallback;
        private static Binding.unitytls_client_data_receive_callback s_recvCallback;

        private static bool IsInitialized;

        private struct ManagedSecureFunctionsKey {}

        internal static readonly SharedStatic<FunctionPointer<Binding.unitytls_client_data_send_callback>>
        s_SendCallback = SharedStatic<FunctionPointer<Binding.unitytls_client_data_send_callback>>
            .GetOrCreate<FunctionPointer<Binding.unitytls_client_data_send_callback>, ManagedSecureFunctionsKey>();

        internal static readonly SharedStatic<FunctionPointer<Binding.unitytls_client_data_receive_callback>>
        s_RecvMethod =
            SharedStatic<FunctionPointer<Binding.unitytls_client_data_receive_callback>>
                .GetOrCreate<FunctionPointer<Binding.unitytls_client_data_receive_callback>, ManagedSecureFunctionsKey>();

        internal static void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;

            unsafe
            {
                s_sendCallback = SecureDataSendCallback;
                s_recvCallback = SecureDataReceiveCallback;

                s_SendCallback.Data = new FunctionPointer<Binding.unitytls_client_data_send_callback>(Marshal.GetFunctionPointerForDelegate(s_sendCallback));
                s_RecvMethod.Data = new FunctionPointer<Binding.unitytls_client_data_receive_callback>(Marshal.GetFunctionPointerForDelegate(s_recvCallback));
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(Binding.unitytls_client_data_send_callback))]
        static unsafe int SecureDataSendCallback(
            IntPtr userData,
            byte* data,
            UIntPtr dataLen,
            uint status)
        {
            var protocolData = (SecureUserData*)userData;
            if (protocolData->Interface.BeginSendMessage.Ptr.Invoke(out var sendHandle,
                protocolData->Interface.UserData, (int)dataLen.ToUInt32()) != 0)
            {
                return UNITYTLS_ERR_SSL_WANT_WRITE;
            }

            sendHandle.size = (int)dataLen.ToUInt32();
            byte* packet = (byte*)sendHandle.data;
            UnsafeUtility.MemCpy(packet, data, (long)dataLen.ToUInt64());

            return protocolData->Interface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref protocolData->Remote,
                protocolData->Interface.UserData, ref protocolData->QueueHandle);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(Binding.unitytls_client_data_receive_callback))]
        static unsafe int SecureDataReceiveCallback(
            IntPtr userData,
            byte* data,
            UIntPtr dataLen,
            uint status)
        {
            var protocolData = (SecureUserData*)userData;
            var packet = (byte*)protocolData->StreamData;
            if (packet == null || protocolData->Size <= 0)
            {
                return UNITYTLS_ERR_SSL_WANT_READ;
            }

            // This is a case where we process an invalid record
            // and the internal statemachine trys to read the next record
            // and we don't have any data. Eventually one side will timeout
            // and resend
            if (protocolData->BytesProcessed != 0)
            {
                return UNITYTLS_ERR_SSL_WANT_READ;
            }

            UnsafeUtility.MemCpy(data, packet, protocolData->Size);
            protocolData->BytesProcessed = protocolData->Size;
            return protocolData->Size;
        }
    }

    /// <summary>
    /// Secure Transport Protocol select the underlying Transport protocol (TCP or UDP)
    /// </summary>
    public enum SecureTransportProtocol : uint
    {
        /// <summary>standard TLS provided for TCP connections</summary>
        TLS = 0,
        /// <summary>standard TLS provided for UDP connections</summary>
        DTLS = 1,
    }

    /// <summary>
    /// Secure client authentication policy
    /// </summary>
    public enum SecureClientAuthPolicy : uint
    {
        /// <summary>peer certificate is not checked</summary>
        /// <remarks>
        /// (default on server)
        /// (insecure on client)
        /// </remarks>
        None = 0,
        /// <summary>peer certificate is checked, however the handshake continues even if verification failed</summary>
        Optional = 1,
        /// <summary>peer *must* present a valid certificate. handshake is aborted if verification failed.</summary>
        Required = 2,
    }

    /// <summary>
    /// The SecureNetworkProtocolParameter are settings used to provide configuration to the underlying
    /// security implementation.
    /// </summary>
    public struct SecureNetworkProtocolParameter : INetworkParameter
    {
        /// <summary>Common (client/server) certificate</summary>
        public FixedString4096Bytes                      Pem;
        /// <summary>Server (or client) own certificate</summary>
        public FixedString4096Bytes                      Rsa;
        /// <summary>Server (or client) own private key</summary>
        public FixedString4096Bytes                      RsaKey;
        /// <summary>Server's hostname's name</summary>
        public FixedString32Bytes                        Hostname;
        /// <summary>Underlying transport protocol provided to tls </summary>
        /// <remarks>
        /// This value is either TLS (used for TCP Connections) or DTLS (used for UDP Connections)
        /// </remarks>
        public SecureTransportProtocol              Protocol;
        /// <summary>server-only policy regarding client authentication</summary>
        /// <remarks>
        /// Default value is optional
        /// </remarks>
        public SecureClientAuthPolicy               ClientAuthenticationPolicy;
        /// <summary>Timeout in ms for ssl reads.</summary>
        public uint                                 SSLReadTimeoutMs;
        /// <summary>Initial ssl handshake maximum timeout value in milliseconds. Default is 60000 (60 seconds)</summary>
        public uint                                 SSLHandshakeTimeoutMax;
        /// <summary>Initial ssl handshake minimum timeout value in milliseconds. Default is 1000 (1 sec)</summary>
        public uint                                 SSLHandshakeTimeoutMin;
    }

    [BurstCompile]
    internal unsafe struct SecureNetworkProtocol : INetworkProtocol
    {
        public IntPtr UserData;

        public static readonly SecureNetworkProtocolParameter DefaultParameters = new SecureNetworkProtocolParameter
        {
            Protocol = SecureTransportProtocol.DTLS,
            SSLReadTimeoutMs = 0,
            SSLHandshakeTimeoutMin = 1000,
            SSLHandshakeTimeoutMax = 60000,
            ClientAuthenticationPolicy = SecureClientAuthPolicy.Optional
        };

        private static void CreateSecureClient(uint role, SecureClientState* state)
        {
            var client = Binding.unitytls_client_create(role, state->ClientConfig);
            state->ClientPtr = client;
        }

        private static Binding.unitytls_client_config* GetSecureClientConfig(SecureNetworkProtocolData * protocolData)
        {
            var config = (Binding.unitytls_client_config*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<Binding.unitytls_client_config>(),
                UnsafeUtility.AlignOf<Binding.unitytls_client_config>(), Allocator.Persistent);

            *config = new Binding.unitytls_client_config();

            Binding.unitytls_client_init_config(config);

            config->dataSendCB = ManagedSecureFunctions.s_SendCallback.Data.Value;
            config->dataReceiveCB = ManagedSecureFunctions.s_RecvMethod.Data.Value;
            config->logCallback = IntPtr.Zero;

            // Going to set this for None for now
            config->clientAuth = Binding.UnityTLSRole_None;

            config->transportProtocol = protocolData->Protocol;
            config->clientAuth = protocolData->ClientAuth;

            config->ssl_read_timeout_ms = protocolData->SSLReadTimeoutMs;
            config->ssl_handshake_timeout_min = protocolData->SSLHandshakeTimeoutMin;
            config->ssl_handshake_timeout_max = protocolData->SSLHandshakeTimeoutMax;

            return config;
        }

        public void Initialize(INetworkParameter[] parameters)
        {
            unsafe
            {
                ManagedSecureFunctions.Initialize();

                //TODO: We need to validate that you have a config that makes sense for what you are trying to do
                // should this be something we allow for expressing in the config? like which role you are?

                // we need Secure Transport related configs because we need the user to pass int he keys?
                if (!TryExtractParameters<SecureNetworkProtocolParameter>(out var secureConfig, parameters))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogWarning("No Secure Protocol configuration parameters were provided");
#endif
                    secureConfig = DefaultParameters;
                }

                // If we have baselib configs we need to make sure they are of proper size
                if (TryExtractParameters<BaselibNetworkParameter>(out var config, parameters))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    // TODO: We do not support fragmented messages at the moment :(
                    // and the largest packet that mbedTLS sends is 1800 which is the key
                    // exchange..
                    if (config.maximumPayloadSize <= 2000)
                    {
                        UnityEngine.Debug.LogWarning(
                            "Secure Protocol Requires the payload size for the Baselib Interface to be at least 2000KB");
                    }
#endif
                }

                if (secureConfig.SSLHandshakeTimeoutMin == 0)
                    secureConfig.SSLHandshakeTimeoutMin = DefaultParameters.SSLHandshakeTimeoutMin;

                if (secureConfig.SSLHandshakeTimeoutMax == 0)
                    secureConfig.SSLHandshakeTimeoutMax = DefaultParameters.SSLHandshakeTimeoutMax;

                UserData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<SecureNetworkProtocolData>(),
                    UnsafeUtility.AlignOf<SecureNetworkProtocolData>(), Allocator.Persistent);
                *(SecureNetworkProtocolData*)UserData = new SecureNetworkProtocolData
                {
                    SecureClients = new UnsafeHashMap<NetworkInterfaceEndPoint, SecureClientState>(1, Allocator.Persistent),
                    Rsa = secureConfig.Rsa,
                    RsaKey = secureConfig.RsaKey,
                    Pem = secureConfig.Pem,
                    Hostname = secureConfig.Hostname,
                    Protocol = (uint)secureConfig.Protocol,
                    SSLReadTimeoutMs = secureConfig.SSLReadTimeoutMs,
                    ClientAuth = (uint)secureConfig.ClientAuthenticationPolicy
                };
            }
        }

        public static void DisposeSecureClient(ref SecureClientState state)
        {
            if (state.ClientConfig->transportUserData.ToPointer() != null)
                UnsafeUtility.Free(state.ClientConfig->transportUserData.ToPointer(), Allocator.Persistent);

            if (state.ClientConfig != null)
                UnsafeUtility.Free((void*)state.ClientConfig, Allocator.Persistent);

            state.ClientConfig = null;

            if (state.ClientPtr != null)
                Binding.unitytls_client_destroy(state.ClientPtr);
        }

        public void Dispose()
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)UserData;
                var keys = protocolData->SecureClients.GetKeyArray(Allocator.Temp);
                for (int connectionIndex = 0; connectionIndex < keys.Length; ++connectionIndex)
                {
                    var connection = protocolData->SecureClients[keys[connectionIndex]];

                    DisposeSecureClient(ref connection);

                    protocolData->SecureClients.Remove(keys[connectionIndex]);
                }

                if (UserData != default)
                    UnsafeUtility.Free(UserData.ToPointer(), Allocator.Persistent);

                UserData = default;
            }
        }

        bool TryExtractParameters<T>(out T config, params INetworkParameter[] param)
        {
            for (var i = 0; i < param.Length; ++i)
            {
                if (param[i] is T)
                {
                    config = (T)param[i];
                    return true;
                }
            }

            config = default;
            return false;
        }

        public int Bind(INetworkInterface networkInterface, ref NetworkInterfaceEndPoint localEndPoint)
        {
            if (networkInterface.Bind(localEndPoint) != 0)
                return -1;

            return 2;
        }

        public int Connect(INetworkInterface networkInterface, NetworkEndPoint endPoint, out NetworkInterfaceEndPoint address)
        {
            return networkInterface.CreateInterfaceEndPoint(endPoint, out address);
        }

        public NetworkEndPoint GetRemoteEndPoint(INetworkInterface networkInterface, NetworkInterfaceEndPoint address)
        {
            return networkInterface.GetGenericEndPoint(address);
        }

        public int Listen(INetworkInterface networkInterface)
        {
            return networkInterface.Listen();
        }

        public NetworkProtocol CreateProtocolInterface()
        {
            return new NetworkProtocol(
                computePacketAllocationSize: new TransportFunctionPointer<NetworkProtocol.ComputePacketAllocationSizeDelegate>(ComputePacketAllocationSize),
                processReceive: new TransportFunctionPointer<NetworkProtocol.ProcessReceiveDelegate>(ProcessReceive),
                processSend: new TransportFunctionPointer<NetworkProtocol.ProcessSendDelegate>(ProcessSend),
                processSendConnectionAccept: new TransportFunctionPointer<NetworkProtocol.ProcessSendConnectionAcceptDelegate>(ProcessSendConnectionAccept),
                processSendConnectionRequest: new TransportFunctionPointer<NetworkProtocol.ProcessSendConnectionRequestDelegate>(ProcessSendConnectionRequest),
                processSendDisconnect: new TransportFunctionPointer<NetworkProtocol.ProcessSendDisconnectDelegate>(ProcessSendDisconnect),
                update: new TransportFunctionPointer<NetworkProtocol.UpdateDelegate>(Update),
                needsUpdate: false,
                userData: UserData,
                maxHeaderSize: UdpCHeader.Length,
                maxFooterSize: 2
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ComputePacketAllocationSizeDelegate))]
        public static int ComputePacketAllocationSize(ref NetworkDriver.Connection connection, IntPtr userData, ref int dataCapacity, out int dataOffset)
        {
            return UnityTransportProtocol.ComputePacketAllocationSize(ref connection, userData, ref dataCapacity, out dataOffset);
        }

        public static bool ServerShouldStep(uint currentState)
        {
            // these are the initial states from the server ?
            switch (currentState)
            {
                case Binding.UNITYTLS_SSL_HANDSHAKE_HELLO_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO_DONE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CHANGE_CIPHER_SPEC:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_FINISHED:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_WRAPUP:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_OVER:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_FLUSH_BUFFERS:
                    return true;
            }

            return false;
        }

        private static bool ClientShouldStep(uint currentState)
        {
            // these are the initial states from the server ?
            switch (currentState)
            {
                case Binding.UNITYTLS_SSL_HANDSHAKE_HELLO_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_HELLO:
                    return true;
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_REQUEST:
                    return false;
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO_DONE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_VERIFY:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_CHANGE_CIPHER_SPEC:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_FINISHED:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_WRAPUP:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_OVER:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_FLUSH_BUFFERS:
                    return true;
            }

            return false;
        }

        internal static void SetSecureUserData(
            IntPtr inStream,
            int size,
            ref NetworkInterfaceEndPoint remote,
            ref NetworkSendInterface networkSendInterface,
            ref NetworkSendQueueHandle queueHandle,
            SecureUserData* secureUserData)
        {
            secureUserData->Interface = networkSendInterface;
            secureUserData->Remote = remote;
            secureUserData->QueueHandle = queueHandle;
            secureUserData->Size = size;
            secureUserData->StreamData = inStream;
            secureUserData->BytesProcessed = 0;
        }

        private static bool CreateNewSecureClientState(
            ref NetworkInterfaceEndPoint endpoint,
            uint tlsRole,
            SecureNetworkProtocolData* protocolData)
        {
            if (protocolData->SecureClients.TryAdd(endpoint, new SecureClientState()))
            {
                var secureClient = protocolData->SecureClients[endpoint];
                secureClient.ClientConfig = GetSecureClientConfig(protocolData);

                CreateSecureClient(tlsRole, &secureClient);

                IntPtr secureUserData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<SecureUserData>(),
                    UnsafeUtility.AlignOf<SecureUserData>(), Allocator.Persistent);

                *(SecureUserData*)secureUserData = new SecureUserData
                {
                    Interface = default,
                    Remote = default,
                    QueueHandle = default,
                    StreamData = IntPtr.Zero,
                    Size = 0,
                    BytesProcessed = 0
                };

                secureClient.ClientConfig->transportUserData = secureUserData;

                secureClient.ClientConfig->hostname = protocolData->Hostname.GetUnsafePtr();
                if (tlsRole == Binding.UnityTLSRole_Server)
                {
                    secureClient.ClientConfig->serverPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->Rsa.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->Rsa.Length)
                    };

                    secureClient.ClientConfig->caPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->Pem.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->Pem.Length)
                    };

                    secureClient.ClientConfig->privateKeyPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->RsaKey.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->RsaKey.Length)
                    };
                }
                else
                {
                    secureClient.ClientConfig->serverPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = null,
                        dataLen = new UIntPtr(0)
                    };

                    secureClient.ClientConfig->caPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->Pem.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->Pem.Length)
                    };

                    secureClient.ClientConfig->privateKeyPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = null,
                        dataLen = new UIntPtr(0)
                    };
                }

                Binding.unitytls_client_init(secureClient.ClientPtr);

                protocolData->SecureClients[endpoint] = secureClient;
            }

            return false;
        }

        internal static uint UpdateSecureHandshakeState(ref SecureClientState clientAgent)
        {
            // So now we need to check which role we are ?
            var isServer = Binding.unitytls_client_get_role(clientAgent.ClientPtr) == Binding.UnityTLSRole_Server;
            // we should do server things
            bool shouldStep = true;
            uint result = Binding.UNITYTLS_HANDSHAKE_STEP;
            do
            {
                shouldStep = false;
                result = Binding.unitytls_client_handshake(
                    clientAgent.ClientPtr);

                // this was a case where properly stepped handshake
                if (result == Binding.UNITYTLS_HANDSHAKE_STEP)
                {
                    uint currentState = Binding.unitytls_client_get_handshake_state(clientAgent.ClientPtr);
                    shouldStep = isServer ? ServerShouldStep(currentState) : ClientShouldStep(currentState);
                }
            }
            while (shouldStep);

            return result;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessReceiveDelegate))]
        public static void ProcessReceive(IntPtr stream, ref NetworkInterfaceEndPoint endpoint, int size,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData,
            ref ProcessPacketCommand command)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;

                // We assume this is a server if we need to create a new SecureClientState and the reason
                // for this is because the client always sends the Connection Request message and we process that
                // and then we check if we have heard from this client before and if not then we need to create one
                // and its assume that client is in the server role and would be validating all incoming connections
                CreateNewSecureClientState(ref endpoint, Binding.UnityTLSRole_Server, protocolData);

                var secureClient = protocolData->SecureClients[endpoint];
                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

                SetSecureUserData(stream, size, ref endpoint, ref sendInterface, ref queueHandle, secureUserData);
                var clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                uint handshakeResult = Binding.UNITYTLS_SUCCESS;

                // check and see if we are still in the handshake :D
                if (clientState == Binding.UnityTLSClientState_Handshake
                    || clientState == Binding.UnityTLSClientState_Init)
                {
                    bool shouldRunAgain = false;
                    do
                    {
                        handshakeResult = UpdateSecureHandshakeState(ref secureClient);
                        clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                        shouldRunAgain = (size != 0 && secureUserData->BytesProcessed == 0 && clientState == Binding.UnityTLSClientState_Handshake);
                    }
                    while (shouldRunAgain);

                    command.Type = ProcessPacketCommandType.Drop;
                }
                else if (clientState == Binding.UnityTLSClientState_Messaging)
                {
                    var buffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
                    var bytesRead = new UIntPtr();
                    var result = Binding.unitytls_client_read_data(secureClient.ClientPtr,
                        (byte*)buffer.GetUnsafePtr(), new UIntPtr(NetworkParameterConstants.MTU),
                        &bytesRead);

                    if (result != Binding.UNITYTLS_SUCCESS)
                    {
                        UnityEngine.Debug.LogError(
                            $"Secure Read Failed with result {result}");

                        // then we have an error we we have failed!
                        command.Type = ProcessPacketCommandType.Drop;
                        return;
                    }

                    UnityTransportProtocol.ProcessReceive((IntPtr)buffer.GetUnsafePtr(),
                        ref endpoint,
                        (int)bytesRead.ToUInt32(),
                        ref sendInterface,
                        ref queueHandle,
                        IntPtr.Zero,
                        ref command);

                    if (command.Type == ProcessPacketCommandType.Disconnect)
                    {
                        // So we got a disconnect message we need to clean up the agent
                        DisposeSecureClient(ref secureClient);

                        protocolData->SecureClients.Remove(endpoint);
                    }
                }

                clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                if (clientState == Binding.UnityTLSClientState_Fail)
                {
                    // In this case we are likely in an error state and we should likely not be getting data from this
                    // client and thus we should Disconnect them.
                    UnityEngine.Debug.LogError(
                        $"Failed to Recv Encrypted Data with result {handshakeResult} on a unauthorized connection");

                    command.Type = ProcessPacketCommandType.Drop;

                    // we should cleanup the connection state ?
                    DisposeSecureClient(ref secureClient);

                    protocolData->SecureClients.Remove(endpoint);
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDelegate))]
        public static int ProcessSend(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkSendInterface sendInterface, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var protocolData = (SecureNetworkProtocolData*)userData;

            CreateNewSecureClientState(ref connection.Address, Binding.UnityTLSRole_Server, protocolData);

            var secureClient = protocolData->SecureClients[connection.Address];
            var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

            SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

            UnityTransportProtocol.WriteSendMessageHeader(ref connection, hasPipeline, ref sendHandle, 0);

            var result = Binding.unitytls_client_send_data(secureClient.ClientPtr, (byte*)sendHandle.data,
                new UIntPtr((uint)sendHandle.size));

            var sendSize = sendHandle.size;

            // we end up having to abort this handle so we can free it up as DTSL will generate a new one
            // based on the encrypted buffer size
            sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);

            if (result != Binding.UNITYTLS_SUCCESS)
            {
                Debug.LogError($"Secure Send failed with result {result}");
                return -1;
            }

            return sendSize;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionAcceptDelegate))]
        public static void ProcessSendConnectionAccept(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                var packet = new NativeArray<byte>(UdpCHeader.Length + 2, Allocator.Temp);
                var size = WriteConnectionAcceptMessage(ref connection, (byte*)packet.GetUnsafePtr(), packet.Length);

                if (size < 0)
                {
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }

                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;
                SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

                var result = Binding.unitytls_client_send_data(secureClient.ClientPtr, (byte*)packet.GetUnsafePtr(), new UIntPtr((uint)packet.Length));
                if (result != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Secure Send failed with result {result}");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        internal static unsafe int WriteConnectionAcceptMessage(ref NetworkDriver.Connection connection, byte* packet, int capacity)
        {
            var size = UdpCHeader.Length;

            if (connection.DidReceiveData == 0)
                size += 2;

            if (size > capacity)
            {
                UnityEngine.Debug.LogError("Failed to create a ConnectionAccept packet: size exceeds capacity");
                return -1;
            }

            var header = (UdpCHeader*)packet;
            *header = new UdpCHeader
            {
                Type = (byte)UdpCProtocol.ConnectionAccept,
                SessionToken = connection.SendToken,
                Flags = 0
            };

            if (connection.DidReceiveData == 0)
            {
                header->Flags |= UdpCHeader.HeaderFlags.HasConnectToken;
                *(ushort*)(packet + UdpCHeader.Length) = connection.ReceiveToken;
            }

            return size;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionRequestDelegate))]
        public static void ProcessSendConnectionRequest(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                CreateNewSecureClientState(ref connection.Address, Binding.UnityTLSRole_Client, protocolData);

                var secureClient = protocolData->SecureClients[connection.Address];

                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

                SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

                var currentState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                // so in this case we are already doing a thing ?
                // FIXME: Reconnect will need to be dealt with here
                if (currentState == Binding.UnityTLSClientState_Handshake)
                    return;

                if (currentState == Binding.UnityTLSClientState_Messaging)
                {
                    // this is the case we are now with a proper handshake!
                    // we now need to send the proper connection request!
                    // FIXME: If using DTLS we should just make that handshake accept the connection
                    var packet = new NativeArray<byte>(UdpCHeader.Length, Allocator.Temp);
                    var header = (UdpCHeader*)packet.GetUnsafePtr();
                    *header = new UdpCHeader
                    {
                        Type = (byte)UdpCProtocol.ConnectionRequest,
                        SessionToken = connection.ReceiveToken,
                        Flags = 0
                    };

                    var result = Binding.unitytls_client_send_data(secureClient.ClientPtr,
                        (byte*)packet.GetUnsafePtr(), new UIntPtr((uint)packet.Length));
                    if (result != Binding.UNITYTLS_SUCCESS)
                    {
                        Debug.LogError("We have failed to Send Encrypted SendConnectionRequest");
                    }

                    return;
                }

                var handshakeResult = UpdateSecureHandshakeState(ref secureClient);
                currentState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                if (currentState == Binding.UnityTLSClientState_Fail)
                {
                    Debug.LogError($"Handshake failed with result {handshakeResult}");

                    // so we are in an error state which likely means the handshake failed in some
                    // way. We dispose of the connection state so when we attempt to connect again
                    // we can try again
                    DisposeSecureClient(ref secureClient);

                    protocolData->SecureClients.Remove(connection.Address);
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDisconnectDelegate))]
        public static void ProcessSendDisconnect(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                if (connection.State == NetworkConnection.State.Connected)
                {
                    var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

                    SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

                    var packet = new NativeArray<byte>(UdpCHeader.Length, Allocator.Temp);

                    var header = (UdpCHeader*)packet.GetUnsafePtr();
                    *header = new UdpCHeader
                    {
                        Type = (byte)UdpCProtocol.Disconnect,
                        SessionToken = connection.SendToken,
                        Flags = 0
                    };

                    var result = Binding.unitytls_client_send_data(secureClient.ClientPtr,
                        (byte*)packet.GetUnsafePtr(), new UIntPtr(UdpCHeader.Length));
                    if (result != Binding.UNITYTLS_SUCCESS)
                    {
                        Debug.LogError("We failed to send Encrypted Disconnect");
                    }
                }

                // we should cleanup the connection state ?
                DisposeSecureClient(ref secureClient);

                protocolData->SecureClients.Remove(connection.Address);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.UpdateDelegate))]
        public static void Update(long updateTime, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            // No-op
        }
    }
}

#endif
