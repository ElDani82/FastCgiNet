using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using FastCgiNet.Logging;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastCgiNet
{
	/// <summary>
	/// This class will provide you a FastCgi hosting application. It will listen and deal with connections while
	/// providing ways for you to know what records different FastCgi connections are receiving.
	/// </summary>
	public class FastCgiHostApplication : IDisposable
	{
		private const int listenBacklog = 500;
		private Socket tcpListenSocket;
		private Socket unixListenSocket;
		private bool SomeListenSocketHasBeenBound
		{
			get
			{
				return tcpListenSocket.IsBound || unixListenSocket.IsBound;
			}
		}
		private ILogger Logger;

		public bool IsRunning { get; private set; }

		/// <summary>
		/// Requests indexed by their sockets.
		/// </summary>
		private ConcurrentDictionary<Socket, Request> openSockets = new ConcurrentDictionary<Socket, Request>();

		#region Events to receive records
		/// <summary>
		/// Upon receiving a record with this event, do not run any blocking code, or the application's main loop will block as well.
		/// </summary>
		public event ReceiveBeginRequestRecord OnReceiveBeginRequestRecord = delegate {};
		/// <summary>
		/// Upon receiving a record with this event, do not run any blocking code, or the application's main loop will block as well.
		/// </summary>
		public event ReceiveParamsRecord OnReceiveParamsRecord = delegate {};
		/// <summary>
		/// Upon receiving a record with this event, do not run any blocking code, or the application's main loop will block as well.
		/// </summary>
		public event ReceiveStdinRecord OnReceiveStdinRecord = delegate {};
		#endregion

		/// <summary>
		/// Associates a socket to a request for the moment.
		/// If there is a different request assigned to this socket, then it is overwritten with this new request.
		/// </summary>
		private void AssociateSocketToRequest(Socket sock, Request req)
		{
			openSockets[sock] = req;
		}

		private void SuperServerLoop()
		{
			//TODO: Leave record building from sockets to another class
			byte[] buffer = new byte[8192];
			int selectMaximumTime = -1;

			var recFactory = new RecordFactory();

			while (true)
			{
				try
				{
					// We want Select to return when either a new connection has arrived or when there is incoming socket data
					List<Socket> socketsReadSet = openSockets.Keys.ToList();
					if (tcpListenSocket.IsBound)
						socketsReadSet.Add(tcpListenSocket);
					if (unixListenSocket.IsBound)
						socketsReadSet.Add(unixListenSocket);

					Socket.Select(socketsReadSet, null, null, selectMaximumTime);
					
					foreach (Socket sock in socketsReadSet)
					{
						// In case this sock is in the read set just because a connection has been accepted,
						// it must be a listen socket and we should accept the new queued connections
						if (sock.IsBound)
						{
							BeginAcceptNewConnections(sock);
							continue;
						}

						// It may also happen that a socket shows up in socketsReadSet but has not yet established
						// a connection (it is in the process of). In that case, skip.
						//if (sock.Connected == false)
						//	continue;

						int minimumNeeded;
						if (Logger != null)
							Logger.Info("Received data through socket {0}", sock.GetHashCode());
						
						// If there is no last incomplete record for this socket, then we need at least 8 bytes for 
						// the header of a first record. Otherwise, we need anything we can get
						RecordBase lastIncompleteRecord = null;
						Request request;
						if (openSockets.ContainsKey (sock))
						{
							request = openSockets[sock];

							if (request != null)
								lastIncompleteRecord = request.LastIncompleteRecord;
							else
							{
								if (Logger != null)
									request = new Request(sock, openSockets, Logger);
								else
									request = new Request(sock, openSockets);

								AssociateSocketToRequest(sock, request);
							}

							if (lastIncompleteRecord == null)
								minimumNeeded = 8;
							else
								minimumNeeded = 1;
						}
						else
						{
							if (Logger != null)
								request = new Request(sock, openSockets, Logger);
							else
								request = new Request(sock, openSockets);

							AssociateSocketToRequest(sock, request);
							minimumNeeded = 8;
						}
		
						// To avoid waiting forever if a request contains less than the necessary amount of bytes
						// and never sends anything else, we check only once if there are more than 8 bytes available,
						// and if there aren't then we continue the foreach loop to the next socket
						int availableBytes = sock.Available;
						if (availableBytes == 0)
						{
							if (Logger != null)
								Logger.Info("Remote socket connection closed for socket {0}. Closing socket and skipping to next Socket.", sock.GetHashCode());

							request.CloseSocket();
							continue;
						}
						else if (availableBytes < minimumNeeded)
						{
							if (Logger != null)
								Logger.Debug("Needed {0} bytes but only got {1}. Skipping to next Socket.", minimumNeeded, availableBytes);

							continue;
						}

						int bytesRead = sock.Receive(buffer, 0, buffer.Length, SocketFlags.None);
						
						// Feed every byte read into records
						int bytesFed = 0;
						while (bytesFed < bytesRead)
						{
							int lastByteOfRecord;
							
							// Are we going to create a new record or feed the last incomplete one?
							if (lastIncompleteRecord == null)
							{
								// There is a possibility of there not being 8 bytes available at this point..
								// If so, we have to wait until the socket has at least 8 bytes available. 
								// Skip to next socket by breaking out of the loop
								if (bytesRead - bytesFed < 8)
								{
									if (Logger != null)
										Logger.Debug("Not enough bytes ({0}) to create a new record. Skipping to next Socket.", bytesRead - bytesFed);

									break;
								}

								if (Logger != null)
									Logger.Debug("Creating new record with {0} bytes still to be fed.", bytesRead - bytesFed);

								//lastIncompleteRecord = new Record (buffer, bytesFed, bytesRead - bytesFed, out lastByteOfRecord);
								lastIncompleteRecord = recFactory.CreateRecordFromHeader(buffer, bytesFed, bytesRead - bytesFed, out lastByteOfRecord);

								if (lastIncompleteRecord.RecordType == RecordType.FCGIBeginRequest)
								{
									// Take this opportunity to feed this beginrequestRecord to the current request
									request.SetBeginRequest((BeginRequestRecord)lastIncompleteRecord);
								}
		
								if (lastByteOfRecord == -1)
								{
									if (Logger != null)
										Logger.Debug("Record is still incomplete. Saving it as last incomplete record for socket {0}. Skipping to next Socket.", sock.GetHashCode());

									request.LastIncompleteRecord = lastIncompleteRecord;
									break;
								}
							}
							else
							{
								if (Logger != null)
									Logger.Debug("Feeding bytes into last incomplete record for socket {0}", sock.GetHashCode());

								lastIncompleteRecord.FeedBytes(buffer, bytesFed, bytesRead - bytesFed, out lastByteOfRecord);
							}
							
							// If we fed all bytes, our record still needs more data that we still haven't received.
							// To give other sockets a chance, break from the loop.
							if (lastByteOfRecord == -1)
							{
								request.LastIncompleteRecord = lastIncompleteRecord;
								if (Logger != null)
									Logger.Debug("Record is still incomplete. Saving it as last incomplete record for socket {0}. Skipping to next Socket.", sock.GetHashCode());

								break;
							}

							if (Logger != null)
								Logger.Debug("Record for socket {0} is complete.", sock.GetHashCode());

							bytesFed = lastByteOfRecord + 1;
							
							// Run the signed events with the complete record
							// Catch application errors to avoid service disruption
							try
							{
								switch (lastIncompleteRecord.RecordType)
								{
									case RecordType.FCGIBeginRequest:
										OnReceiveBeginRequestRecord (request, (BeginRequestRecord)lastIncompleteRecord);
										break;
									
									case RecordType.FCGIParams:
										OnReceiveParamsRecord (request, (ParamsRecord)lastIncompleteRecord);
										break;
									
									case RecordType.FCGIStdin:
										OnReceiveStdinRecord (request, (StdinRecord)lastIncompleteRecord);
										break;
									
									default:
										throw new Exception ("A record of unknown type was received!");
								}
							}
							catch (Exception ex)
							{
								// Log and end request
								if (Logger != null)
									Logger.Error(ex, "Application error");

								request.CloseSocket();
								continue;
							}
							finally
							{
								lastIncompleteRecord = null;
								request.LastIncompleteRecord = null;
								if (Logger != null)
									Logger.Debug("Setting last incomplete record for socket {0} to null.", sock.GetHashCode());
							}
						}
					}
				}
				catch (ObjectDisposedException e)
				{
					if (Logger != null)
						Logger.Info("Some operation was attempted on a closed socket. Exception: {0}", e);
				}
				catch (SocketException e)
				{
					if (e.SocketErrorCode != SocketError.Shutdown)
						throw;

					if (Logger != null)
						Logger.Info("Some operation was attempted on a closed socket. Exception: {0}", e);
				}
				catch (Exception e)
				{
					if (Logger != null)
						Logger.Fatal(e, "Exception would end the data receiving loop. This is extremely bad. Please file a bug report.");
				}
			}
		}

		void OnConnectionAccepted(object sender, SocketAsyncEventArgs e)
		{
			AssociateSocketToRequest(e.AcceptSocket, null);
		}

		/// <summary>
		/// Accepts all pending connections on a socket asynchronously.
		/// </summary>
		void BeginAcceptNewConnections(Socket listenSocket)
		{
			try
			{
				// The commented implementation crashes Mono with a too many heaps warning on Mono 3.0.7... investigate later
				/*
				SocketAsyncEventArgs args;
				do
				{
					args = new SocketAsyncEventArgs();
					args.Completed += OnConnectionAccepted;
				}
				while (listenSocket.AcceptAsync(args) == true);*/

				AssociateSocketToRequest(listenSocket.Accept(), null);
			}
			catch (Exception e)
			{
				if (Logger != null)
					Logger.Error(e);
			}
		}

		/// <summary>
		/// Set this to an <see cref="FastCgiNet.Logging.ILogger"/> to log usage information.
		/// </summary>
		public void SetLogger(ILogger logger)
		{
			if (logger == null)
				throw new ArgumentNullException("logger");

			this.Logger = logger;
		}

		/// <summary>
		/// Defines on what address and what port the TCP socket will listen on.
		/// </summary>
		public void Bind(IPAddress addr, int port)
		{
			tcpListenSocket.Bind(new IPEndPoint(addr, port));
		}

#if __MonoCS__
		/// <summary>
		/// Defines the unix socket path to listen on.
		/// </summary>
		public void Bind(string socketPath)
		{
			var endpoint = new Mono.Unix.UnixEndPoint(socketPath);
			unixListenSocket.Bind (endpoint);
		}
#endif

		/// <summary>
		/// Start this FastCgi application. This method blocks while the program runs.
		/// </summary>
		public void Start() {
			if (!SomeListenSocketHasBeenBound)
				throw new InvalidOperationException("You have to bind to some address or unix socket file first");

			if (tcpListenSocket.IsBound)
				tcpListenSocket.Listen(listenBacklog);
			if (unixListenSocket.IsBound)
				unixListenSocket.Listen(listenBacklog);

			// Wait for connections without blocking
			IsRunning = true;

			// Block here
			SuperServerLoop();
		}

		/// <summary>
		/// Start this FastCgi application. This method does not block and only returns when the server is ready to accept connections.
		/// </summary>
		public void StartInBackground() {
			if (!SomeListenSocketHasBeenBound)
				throw new InvalidOperationException("You have to bind to some address or unix socket file first");

			if (tcpListenSocket.IsBound)
				tcpListenSocket.Listen(listenBacklog);
			if (unixListenSocket.IsBound)
				unixListenSocket.Listen(listenBacklog);

			// Set this before waiting for connections
			IsRunning = true;

			//TODO: If one of the tasks below is delayed (why in the world would that happen, idk) then this
			// method returns without being ready to accept connections..
			Task.Factory.StartNew(SuperServerLoop);
		}

		/// <summary>
		/// Closes the listen socket and all active connections abruptely.
		/// </summary>
		public void Stop()
		{
			IsRunning = false;

			if (tcpListenSocket != null && tcpListenSocket.IsBound)
				tcpListenSocket.Close();
			if (unixListenSocket != null && unixListenSocket.IsBound)
				unixListenSocket.Close();

			//TODO: Stop task that waits for connection data..

			foreach (var socketAndRequest in openSockets)
			{
				socketAndRequest.Key.Close();
			}
		}

		/// <summary>
		/// Stops the server if it hasn't been stopped and disposes of resources, including a logger if one has been set.
		/// </summary>
		public void Dispose()
		{
			Stop();

			if (tcpListenSocket != null)
				tcpListenSocket.Dispose();
			if (unixListenSocket != null)
				unixListenSocket.Dispose();

			if (Logger != null)
			{
				var disposableLogger = Logger as IDisposable;
				if (disposableLogger != null)
					disposableLogger.Dispose();
			}
		}

		public FastCgiHostApplication ()
		{
			tcpListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			unixListenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP); // man unix (7) says most unix implementations are safe on deliveryand order

			IsRunning = false;
		}
	}
}