﻿namespace ReactiveSockets
{
    using System;
    using System.Linq;
    using System.Net.Sockets;
    using System.Threading;
    using ReactiveSockets.Properties;
    using System.IO;
    using Diagnostics;
    using UniRx;

    /// <summary>
    /// Implements the reactive socket base class, which is used 
    /// on the <see cref="IReactiveListener"/> for accepted connections, 
    /// as well as a base class for the <see cref="ReactiveClient"/>.
    /// </summary>
    public class ReactiveSocket : IReactiveSocket, IDisposable
    {
        private static readonly ITracer tracer = Tracer.Get<ReactiveSocket>();

        private bool disposed;
        private TcpClient client;
        // This allows us to write to the underlying socket in a 
        // single-threaded fashion.
        private object syncLock = new object();
        private IDisposable readSubscription;

        // This allows protocols to be easily built by consuming 
        // bytes from the stream using Rx expressions.
        private PseudoBlockingCollection<byte> received = new PseudoBlockingCollection<byte>();

        // The receiver created from the above blocking collection.
        private IObservable<byte> receiver;
        // Used to complete the receiver observable
        private Subject<Unit> receiverTermination = new Subject<Unit>();

        // Subject used to pub/sub sent bytes.
        private ISubject<byte> sender = new Subject<byte>();

        // The default receive buffer size of TcpClient according to
        // http://msdn.microsoft.com/en-us/library/system.net.sockets.tcpclient.receivebuffersize.aspx
        // is 8192 bytes
        private int receiveBufferSize = 8192;

        /// <summary>
        /// Initializes the socket with a previously accepted TCP 
        /// client connection. This overload is used by the <see cref="ReactiveListener"/>.
        /// </summary>
        internal ReactiveSocket(TcpClient client)
            : this()
        {
            tracer.ReactiveSocketCreated();
            Connect(client);
        }

        /// <summary>
        /// Protected constructor used by <see cref="ReactiveClient"/> 
        /// client.
        /// </summary>
        protected internal ReactiveSocket()
        {
            receiver = received.GetConsumingEnumerable().ToObservable(Scheduler.DefaultSchedulers.AsyncConversions)
                .TakeUntil(receiverTermination);
        }

        /// <summary>
        /// Raised when the socket is connected.
        /// </summary>
        public event EventHandler Connected = (sender, args) => { };

        /// <summary>
        /// Raised when the socket is disconnected.
        /// </summary>
        public event EventHandler Disconnected = (sender, args) => { };

        /// <summary>
        /// Raised when the socket is disposed.
        /// </summary>
        public event EventHandler Disposed = (sender, args) => { };

        /// <summary>
        /// Gets whether the socket is connected.
        /// </summary>
        public bool IsConnected { get { return client != null && client.Connected; } }

        /// <summary>
        /// Observable bytes that are being received by this endpoint. Note that 
        /// subscribing to the receiver blocks until a byte is received, so 
        /// subscribers will typically use the extension method <c>SubscribeOn</c> 
        /// to specify the scheduler to use for subscription.
        /// </summary>
        /// <remarks>
        /// This blocking characteristic also propagates to higher level channels built 
        /// on top of this socket, but it's not necessary to use SubscribeOn 
        /// at more than one level.
        /// </remarks>
        public IObservable<byte> Receiver { get { return receiver; } }

        /// <summary>
        /// Observable bytes that are being sent through this endpoint 
        /// by using the <see cref="SendAsync(byte[])"/> or 
        /// <see cref="SendAsync(byte[], CancellationToken)"/>  methods.
        /// </summary>
        public IObservable<byte> Sender { get { return sender; } }

        /// <summary>
        /// Gets or sets the size of the buffer for receiving data.
        /// The default value is 8192 bytes.
        /// </summary>
        public int ReceiveBufferSize
        {
            get { return this.receiveBufferSize; }
            set
            {
                this.receiveBufferSize = value;

                if (this.client != null)
                {
                    this.client.ReceiveBufferSize = value;
                }
            }
        }

        /// <summary>
        /// Gets the TcpClient stream to use. 
        /// </summary>
        /// <remarks>Virtual so it can be overridden to implement SSL</remarks>
        protected virtual System.IO.Stream GetStream()
        {
            return client.GetStream();
        }

        /// <summary>
        /// Connects the reactive socket using the given TCP client.
        /// </summary>
        protected internal void Connect(TcpClient client)
        {
            if (client == null)
                throw new ArgumentNullException("client");

            if (disposed)
            {
                tracer.ReactiveSocketReconnectDisposed();
                throw new ObjectDisposedException(this.ToString());
            }

            if (!client.Connected)
            {
                tracer.ReactiveSocketReceivedDisconnectedTcpClient();
                throw new InvalidOperationException("Client must be connected");
            }

            // We're connecting an already connected client.
            if (this.client == client && client.Connected)
            {
                tracer.ReactiveSocketAlreadyConnected();
                return;
            }

            // We're switching to a new client?
            if (this.client != null && this.client != client)
            {
                tracer.ReactiveSocketSwitchingUnderlyingClient();
                Disconnect();
            }

            this.client = client;
            client.ReceiveBufferSize = receiveBufferSize;

            // Cancel possibly outgoing async work (i.e. reads).
            if (readSubscription != null)
            {
                readSubscription.Dispose();
            }

            // Subscribe to the new client with the new token.
            BeginRead();

            Connected(this, EventArgs.Empty);

            tracer.ReactiveSocketConnected();
        }

        /// <summary>
        /// Disconnects the reactive socket. Throws if not currently connected.
        /// </summary>
        protected void Disconnect()
        {
            if (!IsConnected)
                throw new InvalidOperationException(Strings.TcpClientSocket.DisconnectingNotConnected);

            Disconnect(false);
        }

        /// <summary>
        /// Disconnects the socket, specifying if this is being called 
        /// from Dispose.
        /// </summary>
        protected void Disconnect(bool disposing)
        {
            if (disposed && !disposing)
                throw new ObjectDisposedException(this.ToString());

            if (readSubscription != null)
            {
                readSubscription.Dispose();
            }

            readSubscription = null;

            if (IsConnected)
            {
                client.Close();
                tracer.ReactiveSocketDisconnected();
            }

            client = null;

            Disconnected(this, EventArgs.Empty);
        }

        /// <summary>
        /// Disconnects the socket and releases all resources.
        /// </summary>
        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            Disconnect(true);

            sender.OnCompleted();
            receiverTermination.OnNext(Unit.Default);

            tracer.ReactiveSocketDisposed();

            Disposed(this, EventArgs.Empty);
        }

        private void BeginRead()
        {
            Stream stream = this.GetStream();
            this.readSubscription = Observable.Defer(() =>
                {
                    var buffer = new byte[this.ReceiveBufferSize];

                    return ObservableEx.FromAsyncPattern<byte[], int, int, int>(stream.BeginRead, stream.EndRead)(buffer, 0, buffer.Length)
                        .Select(x => buffer.Take(x).ToArray());
                })
                .Repeat()
                .TakeWhile(x => x.Any())
                .Subscribe(xs => { foreach (var x in xs) {  this.received.Add(x); } }, ex =>
                {
                    tracer.ReactiveSocketReadFailed(ex);
                    Disconnect(false);
                }, () => Disconnect(false));
        }

        /// <summary>
        /// Sends data asynchronously through this endpoint.
        /// </summary>
        public IObservable<Unit> SendAsync(byte[] bytes)
        {
            if (disposed)
            {
                tracer.ReactiveSocketSendDisposed();
                throw new ObjectDisposedException(this.ToString());
            }

            if (!IsConnected)
            {
                tracer.ReactiveSocketSendDisconnected();
                throw new InvalidOperationException("Not connected");
            }

            var stream = this.GetStream();
            return Observable.Start(() =>
            {
                Monitor.Enter(syncLock);
                try { stream.Write(bytes, 0, bytes.Length); }
                finally { Monitor.Exit(syncLock); }
            }, Scheduler.DefaultSchedulers.AsyncConversions)
            .Do(_ => { foreach (var x in bytes) { sender.OnNext(x); } }, ex => Disconnect());
        }

        #region SocketOptions

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.GetSocketOption(SocketOptionLevel, SocketOptionName)" />.</summary>
        public object GetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName)
        {
            return client.Client.GetSocketOption(optionLevel, optionName);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.GetSocketOption(SocketOptionLevel, SocketOptionName, byte[])" />.</summary>
        public void GetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue)
        {
            client.Client.GetSocketOption(optionLevel, optionName, optionValue);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.GetSocketOption(SocketOptionLevel, SocketOptionName, int)" />.</summary>
        public byte[] GetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int optionLength)
        {
            return client.Client.GetSocketOption(optionLevel, optionName, optionLength);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.SetSocketOption(SocketOptionLevel, SocketOptionName, bool)" />.</summary>
        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue)
        {
            client.Client.SetSocketOption(optionLevel, optionName, optionValue);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.SetSocketOption(SocketOptionLevel, SocketOptionName, byte[])" />.</summary>
        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue)
        {
            client.Client.SetSocketOption(optionLevel, optionName, optionValue);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.SetSocketOption(SocketOptionLevel, SocketOptionName, int)" />.</summary>
        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int optionValue)
        {
            client.Client.SetSocketOption(optionLevel, optionName, optionValue);
        }

        /// <summary>See <see cref="T:System.Net.Sockets.Socket.SetSocketOption(SocketOptionLevel, SocketOptionName, object)" />.</summary>
        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, object optionValue)
        {
            client.Client.SetSocketOption(optionLevel, optionName, optionValue);
        }

        #endregion
    }
}
