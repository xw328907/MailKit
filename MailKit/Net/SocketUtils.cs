﻿//
// SocketUtils.cs
//
// Author: Jeffrey Stedfast <jestedfa@microsoft.com>
//
// Copyright (c) 2013-2018 Xamarin Inc. (www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MailKit.Net
{
	static class SocketUtils
	{
#if NETSTANDARD_2_0 || NET_4_5 || __MOBILE__
		class AsyncConnectState
		{
			public readonly CancellationToken CancellationToken;
			public readonly Socket Socket;
			public readonly string Host;
			public readonly int Port;
			public bool IsConnected;
			Exception ex;

			public AsyncConnectState (Socket socket, string host, int port, CancellationToken cancellationToken)
			{
				CancellationToken = cancellationToken;
				Socket = socket;
				Host = host;
				Port = port;
			}

			public void SetCanceled ()
			{
				ex = ex ?? new OperationCanceledException ();
			}

			public void SetException (Exception ex)
			{
				this.ex = this.ex ?? ex;
			}

			public void Throw ()
			{
				if (ex != null)
					throw ex;
			}
		}

		static IAsyncResult BeginConnectAsync (AsyncCallback callback, object state)
		{
			var connect = (AsyncConnectState) state;

			return connect.Socket.BeginConnect (connect.Host, connect.Port, callback, state);
		}

		static void EndConnectAsync (IAsyncResult ar)
		{
			var connect = (AsyncConnectState) ar.AsyncState;

			connect.CancellationToken.ThrowIfCancellationRequested ();
			connect.Socket.EndConnect (ar);
			connect.IsConnected = true;
		}

		// Note: EndConnect needs to catch all exceptions
		static void EndConnect (IAsyncResult ar)
		{
			var connect = (AsyncConnectState) ar.AsyncState;

			if (connect.CancellationToken.IsCancellationRequested) {
				connect.SetCanceled ();
				return;
			}

			try {
				connect.Socket.EndConnect (ar);
				connect.IsConnected = true;
			} catch (Exception ex) {
				connect.SetException (ex);
			}
		}

		static void Wait (IAsyncResult ar, CancellationToken cancellationToken)
		{
			var waitHandles = new WaitHandle[] { ar.AsyncWaitHandle, cancellationToken.WaitHandle };

			WaitHandle.WaitAny (waitHandles);

			cancellationToken.ThrowIfCancellationRequested ();
		}

		public static async Task<Socket> ConnectAsync (string host, int port, IPEndPoint localEndPoint, bool doAsync, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested ();

			var socket = new Socket (SocketType.Stream, ProtocolType.Tcp);

			try {
				if (localEndPoint != null)
					socket.Bind (localEndPoint);

				if (doAsync || cancellationToken.CanBeCanceled) {
					var state = new AsyncConnectState (socket, host, port, cancellationToken);

					if (doAsync) {
						await Task.Factory.FromAsync (BeginConnectAsync, EndConnectAsync, state).ConfigureAwait (false);
					} else {
						var ar = socket.BeginConnect (host, port, EndConnect, state);
						Wait (ar, cancellationToken);
						state.Throw ();
					}
				} else {
					socket.Connect (host, port);
				}
			} catch (OperationCanceledException) {
				if (socket.Connected)
					socket.Disconnect (false);

				socket.Dispose ();
				throw;
			} catch {
				socket.Dispose ();
				throw;
			}

			return socket;
		}
#else // .NETStandard 1.3 and 1.6 do not have Socket.BeginConnect()
		public static async Task<Socket> ConnectAsync (string host, int port, IPEndPoint localEndPoint, bool doAsync, CancellationToken cancellationToken)
		{
			IPAddress[] ipAddresses;
			Socket socket = null;

			if (doAsync) {
				ipAddresses = await Dns.GetHostAddressesAsync (host).ConfigureAwait (false);
			} else {
				ipAddresses = Dns.GetHostAddressesAsync (host).GetAwaiter ().GetResult ();
			}

			for (int i = 0; i < ipAddresses.Length; i++) {
				cancellationToken.ThrowIfCancellationRequested ();

				socket = new Socket (ipAddresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				try {
					if (localEndPoint != null)
						socket.Bind (localEndPoint);

					socket.Connect (ipAddresses[i], port);
				} catch (OperationCanceledException) {
					socket.Dispose ();
					socket = null;
					throw;
				} catch {
					socket.Dispose ();

					if (i + 1 == ipAddresses.Length)
						throw;
				}
			}

			if (socket == null)
				throw new IOException (string.Format ("Failed to resolve host: {0}", host));

			return socket;
		}
#endif

		public static async Task<Socket> ConnectAsync (string host, int port, IPEndPoint localEndPoint, int timeout, bool doAsync, CancellationToken cancellationToken)
		{
			using (var ts = new CancellationTokenSource (timeout)) {
				using (var linked = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, ts.Token)) {
					try {
						return await ConnectAsync (host, port, localEndPoint, doAsync, linked.Token).ConfigureAwait (false);
					} catch (OperationCanceledException) {
						if (!cancellationToken.IsCancellationRequested)
							throw new TimeoutException ();
						throw;
					}
				}
			}
		}

		public static void Poll (Socket socket, SelectMode mode, CancellationToken cancellationToken)
		{
			do {
				cancellationToken.ThrowIfCancellationRequested ();
				// wait 1/4 second and then re-check for cancellation
			} while (!socket.Poll (250000, mode));
		}
	}
}
