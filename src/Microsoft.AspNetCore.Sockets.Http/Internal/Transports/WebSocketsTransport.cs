// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets.Internal.Transports
{
    public class WebSocketsTransport : IHttpTransport
    {
        private readonly WebSocketOptions _options;
        private readonly ILogger _logger;
        private readonly Channel<byte[]> _application;
        private readonly DefaultConnectionContext _connection;
        private readonly CancellationTokenSource _transportCts = new CancellationTokenSource();
        private WebSocket _socket;

        public WebSocketsTransport(WebSocketOptions options, Channel<byte[]> application, DefaultConnectionContext connection, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _options = options;
            _application = application;
            _connection = connection;
            _logger = loggerFactory.CreateLogger<WebSocketsTransport>();
        }

        public async Task ProcessRequestAsync(HttpContext context, CancellationToken token)
        {
            Debug.Assert(context.WebSockets.IsWebSocketRequest, "Not a websocket request");

            using (var ws = await context.WebSockets.AcceptWebSocketAsync(_options.SubProtocol))
            {
                _logger.SocketOpened(_connection.ConnectionId);

                try
                {
                    await ProcessSocketAsync(ws);
                }
                finally
                {
                    _logger.SocketClosed(_connection.ConnectionId);
                }
            }
        }

        public async Task ProcessSocketAsync(WebSocket socket)
        {
            Debug.Assert(_socket == null, "ProcessSocketAsync can only be called once.");
            _socket = socket;

            // Begin sending and receiving. Receiving must be started first because ExecuteAsync enables SendAsync.
            var receiving = StartReceiving(socket, _transportCts.Token);
            var sending = StartSending(socket, _transportCts.Token);

            // Wait for something to shut down.
            var trigger = await Task.WhenAny(
                receiving,
                sending);

            var failed = trigger.IsCanceled || trigger.IsFaulted;
            var task = Task.CompletedTask;
            if (trigger == receiving)
            {
                task = sending;
                _logger.WaitingForSend(_connection.ConnectionId);
            }
            else
            {
                task = receiving;
                _logger.WaitingForClose(_connection.ConnectionId);
            }

            // We're done writing
            _application.Writer.TryComplete();

            await socket.CloseOutputAsync(failed ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, "", _transportCts.Token);

            // Wait for the other task to finish, but abort the transport if the close timeout elapses
            try
            {
                _transportCts.CancelAfter(_options.CloseTimeout);
                using (_transportCts.Token.Register(self => ((WebSocketsTransport)self).CloseTimedOut(), this))
                {
                    await task;
                }
            }
            catch (OperationCanceledException)
            {
                // This occurs when the timeout elapses.
                // We care about it, but only enough to log it, and the Register call above handles that.
            }

            // Observe any exceptions from original completed task
            trigger.GetAwaiter().GetResult();
        }

        private void CloseTimedOut()
        {
            _logger.CloseTimedOut(_connection.ConnectionId);
            _socket.Abort();
        }

        private async Task StartReceiving(WebSocket socket, CancellationToken cancellationToken)
        {
            try
            {
                // REVIEW: This code was copied from the client, it's highly unoptimized at the moment (especially
                // for server logic)
                var incomingMessage = new List<ArraySegment<byte>>();
                while (!cancellationToken.IsCancellationRequested)
                {
                    const int bufferSize = 4096;
                    var totalBytes = 0;
                    WebSocketReceiveResult receiveResult;
                    do
                    {
                        var buffer = new ArraySegment<byte>(new byte[bufferSize]);

                        // Exceptions are handled above where the send and receive tasks are being run.
                        receiveResult = await socket.ReceiveAsync(buffer, cancellationToken);
                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        _logger.MessageReceived(_connection.ConnectionId, receiveResult.MessageType, receiveResult.Count, receiveResult.EndOfMessage);

                        var truncBuffer = new ArraySegment<byte>(buffer.Array, 0, receiveResult.Count);
                        incomingMessage.Add(truncBuffer);
                        totalBytes += receiveResult.Count;
                    } while (!receiveResult.EndOfMessage);

                    // Making sure the message type is either text or binary
                    Debug.Assert((receiveResult.MessageType == WebSocketMessageType.Binary || receiveResult.MessageType == WebSocketMessageType.Text), "Unexpected message type");

                    // TODO: Check received message type against the _options.WebSocketMessageType

                    byte[] messageBuffer = null;

                    if (incomingMessage.Count > 1)
                    {
                        messageBuffer = new byte[totalBytes];
                        var offset = 0;
                        for (var i = 0; i < incomingMessage.Count; i++)
                        {
                            Buffer.BlockCopy(incomingMessage[i].Array, 0, messageBuffer, offset, incomingMessage[i].Count);
                            offset += incomingMessage[i].Count;
                        }
                    }
                    else
                    {
                        messageBuffer = new byte[incomingMessage[0].Count];
                        Buffer.BlockCopy(incomingMessage[0].Array, incomingMessage[0].Offset, messageBuffer, 0, incomingMessage[0].Count);
                    }

                    _logger.MessageToApplication(_connection.ConnectionId, messageBuffer.Length);
                    while (await _application.Writer.WaitToWriteAsync(cancellationToken))
                    {
                        if (_application.Writer.TryWrite(messageBuffer))
                        {
                            incomingMessage.Clear();
                            break;
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Terminating Receive loop due to an error");
                throw;
            }
        }

        private async Task StartSending(WebSocket ws, CancellationToken cancellationToken)
        {
            while (await _application.Reader.WaitToReadAsync(cancellationToken))
            {
                // Get a frame from the application
                while (_application.Reader.TryRead(out var buffer))
                {
                    if (buffer.Length > 0)
                    {
                        try
                        {
                            _logger.SendPayload(_connection.ConnectionId, buffer.Length);

                            var webSocketMessageType = (_connection.TransferMode == TransferMode.Binary
                                ? WebSocketMessageType.Binary
                                : WebSocketMessageType.Text);

                            if (WebSocketCanSend(ws))
                            {
                                await ws.SendAsync(new ArraySegment<byte>(buffer), webSocketMessageType, endOfMessage: true, cancellationToken);
                            }
                        }
                        catch (WebSocketException socketException) when (!WebSocketCanSend(ws))
                        {
                            // this can happen when we send the CloseFrame to the client and try to write afterwards
                            _logger.SendFailed(_connection.ConnectionId, socketException);
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorWritingFrame(_connection.ConnectionId, ex);
                            break;
                        }
                    }
                }
            }
        }

        private static bool WebSocketCanSend(WebSocket ws)
        {
            return !(ws.State == WebSocketState.Aborted ||
                   ws.State == WebSocketState.Closed ||
                   ws.State == WebSocketState.CloseSent);
        }
    }
}
