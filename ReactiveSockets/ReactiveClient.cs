﻿using System;
using System.IO;

namespace ReactiveSockets
{
    using System.Net.Sockets;
    using Diagnostics;
    using UniRx;

    /// <summary>
    /// Implements the <see cref="IReactiveClient"/> over TCP.
    /// </summary>
    public class ReactiveClient : ReactiveSocket, IReactiveClient
    {
        private static readonly ITracer tracer = Tracer.Get<ReactiveClient>();
        private string hostname;
        private int port;
        private readonly Func<Stream, Stream> streamTransform;
        private Stream stream;
        private readonly object getStreamLock = new object();

        /// <summary>
        /// Initializes the reactive client.
        /// </summary>
        /// <param name="hostname">The host name or IP address of the TCP server to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        public ReactiveClient(string hostname, int port) : this(hostname, port, stream => stream) { }

        /// <summary>
        /// Initializes the reactive client using a custom stream transform.
        /// This transform allows using SslStream to provide a secure communication channel to a server
        /// that requires SSL.
        /// </summary>
        /// <param name="hostname">The host name or IP address of the TCP server to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <param name="streamTransform">The callback function to use to obtain the communication <see cref="Stream"/>.
        /// The callback is passed the original Stream from the underlying <see cref="TcpClient"/>.</param>
        /// <example>
        /// Using with SSL:
        /// <code>
        /// var client  = new ReactiveClient(host, port, stream => {
        ///   var ssl = new SslStream(
        ///     stream, 
        ///     userCertificateValidationCallback: (sender, certificate, chain, errors) => true  // ignore SSL cert validation
        ///   );
        ///   ssl.AuthenticateAsClient(host);
        ///   return ssl;
        /// } 
        /// </code>
        /// </example>
        public ReactiveClient(string hostname, int port, Func<Stream, Stream> streamTransform)
        {
            this.hostname = hostname;
            this.port = port;
            this.streamTransform = streamTransform;
            tracer.ReactiveClientCreated(hostname, port);
        }

        /// <summary>
        /// Attemps to connect to the TCP server.
        /// </summary>
        public IObservable<Unit> ConnectAsync()
        {
            var client = new TcpClient();
            return ObservableEx
                .FromAsyncPattern<string, int>(client.BeginConnect, client.EndConnect)(hostname, port)
                .Do(_ => Connect(client));
        }

        /// <summary>
        /// Disconnects the underlying TCP socket.
        /// </summary>
        public new void Disconnect()
        {
            base.Disconnect();
        }

        /// <summary>
        /// Invoke the streamTransform callback to provide a stream to the underlying read/write methods.
        /// </summary>
        /// <returns></returns>
        protected override Stream GetStream()
        {
            lock (getStreamLock)
            {
                return stream ??
                       (stream = (streamTransform == null ? base.GetStream() : streamTransform(base.GetStream())));
            }
        }
    }
}
