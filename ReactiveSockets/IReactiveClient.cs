﻿namespace ReactiveSockets
{
    using System;
    using UniRx;

    /// <summary>
    /// Interface implemented by the reactive client socket that can 
    /// connect, send data to and receive data from a server.
    /// </summary>
    interface IReactiveClient : IReactiveSocket
    {
        /// <summary>
        /// Attempts to connect to a server.
        /// </summary>
        IObservable<Unit> ConnectAsync();

        /// <summary>
        /// Disconnects the underlying connection to the server.
        /// </summary>
        void Disconnect();
    }
}
