﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Buttplug.Core;
using Buttplug.Core.Messages;
using JetBrains.Annotations;
using static Buttplug.Client.DeviceEventArgs;

namespace Buttplug.Client
{
    public class ButtplugWSClient
    {
        [NotNull]
        private readonly ButtplugJsonMessageParser _parser;

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

        [NotNull]
        private ConcurrentDictionary<uint, ButtplugClientDevice> _devices = new ConcurrentDictionary<uint, ButtplugClientDevice>();

        [CanBeNull]
        private ClientWebSocket _ws;

        [CanBeNull]
        private Timer _pingTimer;

        [CanBeNull]
        private Task _readThread;

        private CancellationTokenSource _tokenSource;

        private Dispatcher _owningDispatcher;

        [NotNull]
        private int _counter;

        [CanBeNull]
        public event EventHandler<DeviceEventArgs> DeviceAdded;

        [CanBeNull]
        public event EventHandler<DeviceEventArgs> DeviceRemoved;

        [CanBeNull]
        public event EventHandler<ScanningFinishedEventArgs> ScanningFinished;

        [CanBeNull]
        public event EventHandler<ErrorEventArgs> ErrorReceived;

        [CanBeNull]
        public event EventHandler<LogEventArgs> Log;

        [CanBeNull]
        private bool _gotServerInfo;

        [CanBeNull]
        private bool _gotError;

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
            _owningDispatcher = Dispatcher.CurrentDispatcher;
            _tokenSource = new CancellationTokenSource();
        }

        ~ButtplugWSClient()
        {
            Disconnect().Wait();
        }

        public async Task Connect(Uri aURL)
        {
            if (_ws != null && (_ws.State == WebSocketState.Connecting || _ws.State == WebSocketState.Open))
            {
                throw new AccessViolationException("Already connected!");
            }

            _ws = new ClientWebSocket();
            _waitingMsgs.Clear();
            _devices.Clear();
            _counter = 1;
            _gotServerInfo = false;
            _gotError = false;
            await _ws.ConnectAsync(aURL, CancellationToken.None);

            if (_ws.State != WebSocketState.Open)
            {
                throw new Exception("Connection failed!");
            }

            _readThread = new Task(() => { wsReader(_tokenSource.Token); }, _tokenSource.Token, TaskCreationOptions.LongRunning);
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
                    throw new Exception(e.ErrorMessage);

                default:
                    throw new Exception("Unexpecte message returned: " + res.GetType().ToString());
            }
        }

        public async Task Disconnect()
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
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client shutdown", _tokenSource.Token);
                }
            }

            _tokenSource.Cancel();
            _readThread.Wait();

            var max = 3;
            while (max-- > 0 && _waitingMsgs.Count != 0)
            {
                foreach (var msgId in _waitingMsgs.Keys)
                {
                    if (_waitingMsgs.TryRemove(msgId, out TaskCompletionSource<ButtplugMessage> promise))
                    {
                        promise.SetResult(new Error("Connection closed!", Error.ErrorClass.ERROR_UNKNOWN, ButtplugConsts.SystemMsgId));
                    }
                }
            }

            _counter = 1;
        }

        private async void wsReader(CancellationToken aToken)
        {
            var sb = new StringBuilder();
            while (_ws != null && _ws.State == WebSocketState.Open && !aToken.IsCancellationRequested)
            {
                try
                {
                    var buffer = new byte[5];
                    var segment = new ArraySegment<byte>(buffer);
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _ws.ReceiveAsync(segment, aToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // If the operation is cancelled, just continue so we fall out of the loop
                        continue;
                    }

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

                            switch (msg)
                            {
                                case Log l:
                                    _owningDispatcher.Invoke(() =>
                                    {
                                        Log?.Invoke(this, new LogEventArgs(l));
                                    });
                                    break;

                                case DeviceAdded d:
                                    var dev = new ButtplugClientDevice(d);
                                    _devices.AddOrUpdate(d.DeviceIndex, dev, (idx, old) => dev);
                                    _owningDispatcher.Invoke(() =>
                                    {
                                        DeviceAdded?.Invoke(this, new DeviceEventArgs(dev, DeviceAction.ADDED));
                                    });
                                    break;

                                case DeviceRemoved d:
                                    if (_devices.TryRemove(d.DeviceIndex, out ButtplugClientDevice oldDev))
                                    {
                                        _owningDispatcher.Invoke(() =>
                                        {
                                            DeviceRemoved?.Invoke(this, new DeviceEventArgs(oldDev, DeviceAction.REMOVED));
                                        });
                                    }

                                    break;

                                case ScanningFinished sf:
                                    _owningDispatcher.Invoke(() =>
                                    {
                                        ScanningFinished?.Invoke(this, new ScanningFinishedEventArgs(sf));
                                    });
                                    break;

                                case Error e:
                                    _owningDispatcher.Invoke(() =>
                                    {
                                        ErrorReceived?.Invoke(this, new ErrorEventArgs(e));
                                    });
                                    break;
                            }
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
            try
            {
                var msg = await SendMessage(new Ping(nextMsgId));
                if (msg is Error)
                {
                    _owningDispatcher.Invoke(() =>
                    {
                        ErrorReceived?.Invoke(this, new ErrorEventArgs(msg as Error));
                    });
                    throw new Exception((msg as Error).ErrorMessage);
                }
            }
            catch
            {
                if (_ws != null)
                {
                    await Disconnect();
                }
            }
        }

        public async Task RequestDeviceList()
        {
            var resp = await SendMessage(new RequestDeviceList(nextMsgId));
            if (!(resp is DeviceList) || (resp as DeviceList).Devices == null)
            {
                if (resp is Error)
                {
                    _owningDispatcher.Invoke(() =>
                    {
                        ErrorReceived?.Invoke(this, new ErrorEventArgs(resp as Error));
                    });
                }

                return;
            }

            foreach (var d in (resp as DeviceList).Devices)
            {
                if (!_devices.ContainsKey(d.DeviceIndex))
                {
                    var device = new ButtplugClientDevice(d);
                    if (_devices.TryAdd(d.DeviceIndex, device))
                    {
                        _owningDispatcher.Invoke(() =>
                        {
                            DeviceAdded?.Invoke(this, new DeviceEventArgs(device, DeviceAction.ADDED));
                        });
                    }
                }
            }
        }

        public ButtplugClientDevice[] getDevices()
        {
            var devices = new List<ButtplugClientDevice>();
            devices.AddRange(_devices.Values);
            return devices.ToArray();
        }

        public async Task<bool> StartScanning()
        {
            return await SendMessageExpectOk(new StartScanning(nextMsgId));
        }

        public async Task<bool> StopScanning()
        {
            return await SendMessageExpectOk(new StopScanning(nextMsgId));
        }

        public async Task<bool> RequestLog(string aLogLevel)
        {
            return await this.SendMessageExpectOk(new RequestLog(aLogLevel, nextMsgId));
        }

        public async Task<ButtplugMessage> SendDeviceMessage(ButtplugClientDevice aDevice, ButtplugDeviceMessage aDeviceMsg)
        {
            if (_devices.TryGetValue(aDevice.Index, out ButtplugClientDevice dev))
            {
                if (!dev.AllowedMessages.Contains(aDeviceMsg.GetType().Name))
                {
                    return new Error("Device does not accept message type: " + aDeviceMsg.GetType().Name, Error.ErrorClass.ERROR_DEVICE, ButtplugConsts.SystemMsgId);
                }

                aDeviceMsg.DeviceIndex = aDevice.Index;
                return await SendMessage(aDeviceMsg);
            }
            else
            {
                return new Error("Device not available.", Error.ErrorClass.ERROR_DEVICE, ButtplugConsts.SystemMsgId);
            }
        }

        protected async Task<bool> SendMessageExpectOk(ButtplugMessage aMsg)
        {
            return await SendMessage(aMsg) is Ok;
        }

        protected async Task<ButtplugMessage> SendMessage(ButtplugMessage aMsg)
        {
            var promise = new TaskCompletionSource<ButtplugMessage>();
            _waitingMsgs.TryAdd(aMsg.Id, promise);

            var output = Serialize(aMsg);
            var segment1 = new ArraySegment<byte>(Encoding.UTF8.GetBytes(output));

            try
            {
                lock (sendLock)
                {
                    if (_ws != null && _ws.State == WebSocketState.Open)
                    {
                        _ws.SendAsync(segment1, WebSocketMessageType.Text, true, _tokenSource.Token).Wait();
                    }
                    else
                    {
                        return new Error("Bad WS state!", Error.ErrorClass.ERROR_UNKNOWN, ButtplugConsts.SystemMsgId);
                    }
                }

                return await promise.Task;
            }
            catch (WebSocketException e)
            {
                // Noop - WS probably closed on us during read
                return new Error(e.Message, Error.ErrorClass.ERROR_UNKNOWN, ButtplugConsts.SystemMsgId);
            }
        }

        protected string Serialize(ButtplugMessage aMsg)
        {
            return _parser.Serialize(aMsg);
        }

        protected string Serialize(ButtplugMessage[] aMsgs)
        {
            return _parser.Serialize(aMsgs);
        }

        protected ButtplugMessage[] Deserialize(string aMsg)
        {
            return _parser.Deserialize(aMsg);
        }
    }
}
