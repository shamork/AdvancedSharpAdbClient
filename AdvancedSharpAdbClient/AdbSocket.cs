﻿// <copyright file="AdbSocket.cs" company="The Android Open Source Project, Ryan Conrad, Quamotion, yungd1plomat, wherewhere">
// Copyright (c) The Android Open Source Project, Ryan Conrad, Quamotion, yungd1plomat, wherewhere. All rights reserved.
// </copyright>

using AdvancedSharpAdbClient.Exceptions;
using AdvancedSharpAdbClient.Logs;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdvancedSharpAdbClient
{

    /// <summary>
    /// <para>Implements a client for the Android Debug Bridge client-server protocol. Using the client, you
    /// can send messages to and receive messages from the Android Debug Bridge.</para>
    /// <para>The <see cref="AdbSocket"/> class implements the raw messaging protocol; that is,
    /// sending and receiving messages. For interacting with the services the Android Debug
    /// Bridge exposes, use the <see cref="AdbClient"/>.</para>
    /// <para>For more information about the protocol that is implemented here, see chapter
    /// II Protocol Details, section 1. Client &lt;-&gt;Server protocol at
    /// <see href="https://android.googlesource.com/platform/system/core/+/master/adb/OVERVIEW.TXT"/>.</para>
    /// </summary>
    public partial class AdbSocket : IAdbSocket
    {
        /// <summary>
        /// The underlying TCP socket that manages the connection with the ADB server.
        /// </summary>
        protected readonly ITcpSocket socket;

#if HAS_LOGGER
        /// <summary>
        /// The logger to use when logging messages.
        /// </summary>
        private readonly ILogger<AdbSocket> logger = LoggerProvider.CreateLogger<AdbSocket>();
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="AdbSocket"/> class.
        /// </summary>
        /// <param name="endPoint">The <see cref="EndPoint"/> at which the Android Debug Bridge is listening for clients.</param>
        public AdbSocket(EndPoint endPoint)
        {
            socket = new TcpSocket();
            socket.Connect(endPoint);
            socket.ReceiveBufferSize = ReceiveBufferSize;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AdbSocket"/> class.
        /// </summary>
        /// <param name="host">The host address at which the Android Debug Bridge is listening for clients.</param>
        /// <param name="port">The port at which the Android Debug Bridge is listening for clients.</param>
        public AdbSocket(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentNullException(nameof(host));
            }

            string[] values = host.Split(':');

            DnsEndPoint endPoint = values.Length <= 0
                ? throw new ArgumentNullException(nameof(host))
                : new DnsEndPoint(values[0], values.Length > 1 && int.TryParse(values[1], out int _port) ? _port : port);

            socket = new TcpSocket();
            socket.Connect(endPoint);
            socket.ReceiveBufferSize = ReceiveBufferSize;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AdbSocket"/> class.
        /// </summary>
        /// <param name="socket">The <see cref="ITcpSocket"/> at which the Android Debug Bridge is listening for clients.</param>
        public AdbSocket(ITcpSocket socket) => this.socket = socket;

        /// <summary>
        /// Gets or sets the size of the receive buffer
        /// </summary>
        public static int ReceiveBufferSize { get; set; } = 40960;

        /// <summary>
        /// Gets or sets the size of the write buffer.
        /// </summary>
        public static int WriteBufferSize { get; set; } = 1024;

        /// <summary>
        /// Determines whether the specified reply is okay.
        /// </summary>
        /// <param name="reply">The reply.</param>
        /// <returns><see langword="true"/> if the specified reply is okay; otherwise, <see langword="false"/>.</returns>
        public static bool IsOkay(byte[] reply) => AdbClient.Encoding.GetString(reply).Equals("OKAY");

        /// <inheritdoc/>
        public bool Connected => socket.Connected;

        /// <inheritdoc/>
        public virtual void Reconnect() => socket.Reconnect();

        /// <inheritdoc/>
        public void Send(byte[] data, int length) => Send(data, 0, length);

        /// <inheritdoc/>
        public virtual void Send(byte[] data, int offset, int length)
        {
            try
            {
                int count = socket.Send(data, 0, length != -1 ? length : data.Length, SocketFlags.None);
                if (count < 0)
                {
                    AdbException ex = new("channel EOF");
#if HAS_LOGGER
                    logger.LogError(ex, ex.Message);
#endif
                    throw ex;
                }
            }
#if HAS_LOGGER
            catch (SocketException sex)
            {
                logger.LogError(sex, sex.Message);
#else
            catch (SocketException)
            {
#endif
                throw;
            }
        }

        /// <inheritdoc/>
        public void SendSyncRequest(SyncCommand command, string path, int permissions) =>
            SendSyncRequest(command, $"{path},{permissions}");

        /// <inheritdoc/>
        public virtual void SendSyncRequest(SyncCommand command, string path)
        {
            ExceptionExtensions.ThrowIfNull(path);
            byte[] pathBytes = AdbClient.Encoding.GetBytes(path);
#if HAS_LOGGER
            logger.LogInformation("Send sync request: {command} {path}", command, path);
#endif
            SendSyncRequest(command, pathBytes.Length);
            _ = Write(pathBytes);
        }

        /// <inheritdoc/>
        public virtual void SendSyncRequest(SyncCommand command, int length)
        {
            // The message structure is:
            // First four bytes: command
            // Next four bytes: length of the path
            // Final bytes: path
            byte[] commandBytes = SyncCommandConverter.GetBytes(command);

            byte[] lengthBytes = BitConverter.GetBytes(length);

            if (!BitConverter.IsLittleEndian)
            {
                // Convert from big endian to little endian
                Array.Reverse(lengthBytes);
            }
#if HAS_LOGGER
            logger.LogInformation("Send sync request: {command}", command);
#endif
            _ = Write(commandBytes);
            _ = Write(lengthBytes);
        }

        /// <inheritdoc/>
        public virtual void SendAdbRequest(string request)
        {
#if HAS_LOGGER
            logger.LogInformation("Send adb request: {request}", request);
#endif
            byte[] data = AdbClient.FormAdbRequest(request);

            if (!Write(data))
            {
                IOException ex = new($"Failed sending the request '{request}' to ADB");
#if HAS_LOGGER
                logger.LogError(ex, ex.Message);
#endif
                throw ex;
            }
        }

        /// <inheritdoc/>
        public int Read(byte[] data) => Read(data, data.Length);

        /// <inheritdoc/>
        public virtual int Read(byte[] data, int length)
        {
            int expLen = length != -1 ? length : data.Length;
            int count = -1;
            int totalRead = 0;

            while (count != 0 && totalRead < expLen)
            {
                try
                {
                    int left = expLen - totalRead;
                    int bufferLength = left < ReceiveBufferSize ? left : ReceiveBufferSize;

                    byte[] buffer = new byte[bufferLength];
                    count = socket.Receive(buffer, bufferLength, SocketFlags.None);
                    if (count < 0)
                    {
                        AdbException ex = new("EOF");
#if HAS_LOGGER
                        logger.LogError(ex, "read: channel EOF");
#endif
                        throw ex;
                    }
                    else if (count == 0)
                    {
#if HAS_LOGGER
                        logger.LogInformation("DONE with Read");
#endif
                    }
                    else
                    {
                        Array.Copy(buffer, 0, data, totalRead, count);
                        totalRead += count;
                    }
                }
                catch (SocketException sex)
                {
                    AdbException ex = new($"No Data to read: {sex.Message}");
#if HAS_LOGGER
                    logger.LogError(sex, ex.Message);
#endif
                    throw ex;
                }
            }

            return totalRead;
        }

        /// <inheritdoc/>
        public virtual string ReadString()
        {
            // The first 4 bytes contain the length of the string
            byte[] reply = new byte[4];
            int read = Read(reply);

            if (read == 0)
            {
                // There is no data to read
                return null;
            }

            // Convert the bytes to a hex string
            string lenHex = AdbClient.Encoding.GetString(reply);
            int len = int.Parse(lenHex, NumberStyles.HexNumber);

            // And get the string
            reply = new byte[len];
            _ = Read(reply);

            string value = AdbClient.Encoding.GetString(reply);
#if HAS_LOGGER
            logger.LogInformation("Read string: {value}", value);
#endif
            return value;
        }

        /// <inheritdoc/>
        public virtual string ReadSyncString()
        {
            // The first 4 bytes contain the length of the string
            byte[] reply = new byte[4];
            _ = Read(reply);

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(reply);
            }

            int len = BitConverter.ToInt32(reply, 0);

            // And get the string
            reply = new byte[len];
            _ = Read(reply);

            string value = AdbClient.Encoding.GetString(reply);
#if HAS_LOGGER
            logger.LogInformation("Read sync string: {value}", value);
#endif
            return value;
        }

        /// <inheritdoc/>
        public virtual SyncCommand ReadSyncResponse()
        {
            byte[] data = new byte[4];
            _ = Read(data);
            SyncCommand value = SyncCommandConverter.GetCommand(data);
#if HAS_LOGGER
            logger.LogInformation("Read sync response: {value}", value);
#endif
            return value;
        }

        /// <inheritdoc/>
        public virtual AdbResponse ReadAdbResponse()
        {
            AdbResponse response = ReadAdbResponseInner();
            if (!response.IOSuccess || !response.Okay)
            {
                socket.Dispose();
                AdbException ex = new($"An error occurred while reading a response from ADB: {response.Message}", response);
#if HAS_LOGGER
                logger.LogError(ex, ex.Message);
#endif
                throw ex;
            }
#if HAS_LOGGER
            logger.LogInformation("Read adb response: {response}", response.Message);
#endif
            return response;
        }

        /// <inheritdoc/>
        public Stream GetShellStream()
        {
            Stream stream = socket.GetStream();
            return new ShellStream(stream, closeStream: true);
        }

        /// <inheritdoc/>
        public void SetDevice(DeviceData device)
        {
            // if the device is not null, then we first tell adb we're looking to talk
            // to a specific device
            if (device != null)
            {
                SendAdbRequest($"host:transport:{device.Serial}");

                try
                {
                    AdbResponse response = ReadAdbResponse();
                }
                catch (AdbException e)
                {
                    if (string.Equals("device not found", e.AdbError, StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceNotFoundException ex = new(device.Serial);
#if HAS_LOGGER
                        logger.LogError(ex, ex.Message);
#endif
                        throw ex;
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Write until all data in "data" is written or the connection fails or times out.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <returns>Returns <see langword="true"/> if all data was written; otherwise, <see langword="false"/>.</returns>
        /// <remarks>This uses the default time out value.</remarks>
        protected virtual bool Write(byte[] data)
        {
            try
            {
                Send(data, -1);
            }
#if HAS_LOGGER
            catch (IOException e)
            {
                logger.LogError(e, e.Message);
#else
            catch (IOException)
            {
#endif
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the response from ADB after a command.
        /// </summary>
        /// <returns>A <see cref="AdbResponse"/> that represents the response received from ADB.</returns>
        protected virtual AdbResponse ReadAdbResponseInner()
        {
            AdbResponse rasps = new();

            byte[] reply = new byte[4];
            Read(reply);

            rasps.IOSuccess = true;

            rasps.Okay = IsOkay(reply);

            if (!rasps.Okay)
            {
                string message = ReadString();
                rasps.Message = message;
#if HAS_LOGGER
                logger.LogWarning($"Got reply '{ReplyToString(reply)}', diag='{rasps.Message}'");
#endif
            }

            return rasps;
        }

        /// <summary>
        /// Converts an ADB reply to a string.
        /// </summary>
        /// <param name="reply">A <see cref="byte"/> array that represents the ADB reply.</param>
        /// <returns>A <see cref="string"/> that represents the ADB reply.</returns>
        protected virtual string ReplyToString(byte[] reply)
        {
            string result;
            try
            {
                result = Encoding.ASCII.GetString(reply);
            }
#if HAS_LOGGER
            catch (DecoderFallbackException e)
            {
                logger.LogWarning(e, e.Message);
#else
            catch (DecoderFallbackException)
            {
#endif
                result = string.Empty;
            }

            return result;
        }

        /// <summary>
        /// Releases all resources used by the current instance of the <see cref="AdbSocket"/> class.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                socket.Dispose();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
