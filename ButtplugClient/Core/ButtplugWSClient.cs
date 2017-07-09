﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Buttplug.Core;
using Buttplug.Messages;
using JetBrains.Annotations;

namespace ButtplugClient.Core
{
    public class ButtplugWSClient
    {
        [NotNull]
        private readonly ButtplugJsonMessageParser _parser;

        [CanBeNull]
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        [NotNull]
        private readonly IButtplugLog _bpLogger;

        [NotNull]
        private readonly IButtplugLogManager _bpLogManager;

        private readonly object sendLock = new object();

        [NotNull]
        private readonly string _clientName;

        [NotNull]
        private readonly uint _messageSchemaVersion;

        [NotNull]
        private ConcurrentDictionary<uint, TaskCompletionSource<ButtplugMessage>> _waitingMsgs = new ConcurrentDictionary<uint, TaskCompletionSource<ButtplugMessage>>();

        [CanBeNull]
        private ClientWebSocket _ws;

        [CanBeNull]
        private Timer _pingTimer;

        [CanBeNull]
        private Thread _readThread;

        [NotNull]
        private int _counter;

        public uint nextMsgId
        {
            get
            {
                return Convert.ToUInt32(Interlocked.Increment(ref _counter));
            }
        }

        public ButtplugWSClient(string aClientName)
        {
            _clientName = aClientName;
            _bpLogManager = new ButtplugLogManager();
            _bpLogger = _bpLogManager.GetLogger(GetType());
            _parser = new ButtplugJsonMessageParser(_bpLogManager);
            _bpLogger.Trace("Finished setting up ButtplugClient");
        }

        ~ButtplugWSClient()
        {
            Diconnect().Wait();
        }

        public async Task Connect(Uri aURL)
        {
            if (_ws != null && (_ws.State == WebSocketState.Connecting || _ws.State == WebSocketState.Open) )
            {
                throw new AccessViolationException("Already connected!");
            }

            _ws = new ClientWebSocket();
            _waitingMsgs.Clear();
            _counter = 1;
            await _ws.ConnectAsync(aURL, CancellationToken.None);

            if (_ws.State != WebSocketState.Open)
            {
                throw new Exception("Connection failed!");
            }

            _readThread = new Thread(wsReader);
            _readThread.Start();

            var res = await SendMessage(new RequestServerInfo(_clientName));
            switch (res)
            {
                case ServerInfo si:
                    if (si.MaxPingTime > 0)
                    {
                        _pingTimer = new Timer(onPingTimer, null, 0, Convert.ToInt32(Math.Round(((double)si.MaxPingTime) / 2, 0)));
                    }

                    break;

                case Error e:
                    break;
            }
        }

        public async Task Diconnect()
        {
            if (_pingTimer != null)
            {
                _pingTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _pingTimer = null;
            }

            while (_ws != null && _ws.State != WebSocketState.Closed && _ws.State != WebSocketState.Aborted)
            {
                if (_ws.State != WebSocketState.CloseSent && _ws.State != WebSocketState.Closed)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutdown", CancellationToken.None);
                }
            }

            if (_readThread != null && _readThread.IsAlive)
            {
                _readThread.Join();
                _readThread = null;
            }

            _counter = 1;
        }

        private async void wsReader()
        {
            var sb = new StringBuilder();
            while (_ws != null && _ws.State == WebSocketState.Open)
            {
                try
                {
                    var buffer = new byte[5];
                    var segment = new ArraySegment<byte>(buffer);
                    var result = await _ws.ReceiveAsync(segment, CancellationToken.None);
                    var input = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    sb.Append(input);
                    if (result.EndOfMessage)
                    {
                        var msgs = Deserialize(sb.ToString());
                        foreach (var msg in msgs)
                        {
                            if (msg.Id > 0 && _waitingMsgs.TryRemove(msg.Id, out TaskCompletionSource<ButtplugMessage> queued))
                            {
                                queued.TrySetResult(msg);
                                continue;
                            }

                            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(msg));
                        }

                        sb.Clear();
                    }
                }
                catch (WebSocketException)
                {
                    // Noop - WS probably closed on us during read
                }
            }
        }

        private async void onPingTimer(object state)
        {
            var msg = await SendMessage(new Ping(nextMsgId));
            if (msg is Error)
            {
                // Do something with the error!
            }
        }

        public async Task<ButtplugMessage> SendMessage(ButtplugMessage aMsg)
        {
            var promise = new TaskCompletionSource<ButtplugMessage>();
            _waitingMsgs.TryAdd(aMsg.Id, promise);

            var output = Serialize(aMsg);
            var segment1 = new ArraySegment<byte>(Encoding.UTF8.GetBytes(output));

            try
            {
                lock (sendLock)
                {
                    _ws.SendAsync(segment1, WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                }

                return await promise.Task;
            }
            catch (WebSocketException)
            {
                // Noop - WS probably closed on us during read
                return null;
            }
        }

        public string Serialize(ButtplugMessage aMsg)
        {
            return _parser.Serialize(aMsg);
        }

        public string Serialize(ButtplugMessage[] aMsgs)
        {
            return _parser.Serialize(aMsgs);
        }

        public ButtplugMessage[] Deserialize(string aMsg)
        {
            return _parser.Deserialize(aMsg);
        }
    }
}