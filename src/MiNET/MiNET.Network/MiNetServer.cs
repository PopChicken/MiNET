using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Timers;
using Craft.Net.TerrainGeneration;

namespace MiNET.Network
{
	public class MiNetServer
	{
		private const int DefaultPort = 19132;

		private int _sequenceNumber;
		private char _reliableMessageNumber;
		private IPEndPoint _endpoint;
		private UdpClient _listener;
		private StandardGenerator _generator;
		private Queue<Tuple<IPEndPoint, byte[]>> sendQueue = new Queue<Tuple<IPEndPoint, byte[]>>();
		private Dictionary<IPEndPoint, Player> _playerEndpoints;
		private Level _level;

		public MiNetServer(int port) : this(new IPEndPoint(IPAddress.Any, port))
		{
		}

		public MiNetServer(IPEndPoint endpoint = null)
		{
			_endpoint = endpoint ?? new IPEndPoint(IPAddress.Any, DefaultPort);
		}

		public bool StartServer()
		{
			if (_listener != null) return false; // Already started

			try
			{
				_playerEndpoints = new Dictionary<IPEndPoint, Player>();

				_level = new Level("Default");
				_level.Initialize();

				_listener = new UdpClient(_endpoint);

				// SIO_UDP_CONNRESET (opcode setting: I, T==3)
				// Windows:  Controls whether UDP PORT_UNREACHABLE messages are reported.
				// - Set to TRUE to enable reporting.
				// - Set to FALSE to disable reporting.

				uint IOC_IN = 0x80000000;
				uint IOC_VENDOR = 0x18000000;
				uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
				_listener.Client.IOControl((int) SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);

				// We need to catch errors here to remove the code above.
				_listener.BeginReceive(ReceiveCallback, _listener);

				Timer sendTimer = new Timer(30);
				sendTimer.AutoReset = true;
				sendTimer.Elapsed += SendTimerElapsed;
				sendTimer.Start();

				Console.WriteLine("Server open for business...");

				return true;
			}
			catch (Exception e)
			{
				Debug.Write(e);
				StopServer();
			}

			return false;
		}


		public bool StopServer()
		{
			try
			{
				if (_listener == null) return true; // Already stopped. It's ok.

				_listener.Close();
				_listener = null;

				return true;
			}
			catch (Exception e)
			{
				Debug.Write(e);
			}

			return true;
		}

		private void ReceiveCallback(IAsyncResult ar)
		{
			try
			{
				UdpClient listener = (UdpClient) ar.AsyncState;

				// WSAECONNRESET:
				// The virtual circuit was reset by the remote side executing a hard or abortive close. 
				// The application should close the socket; it is no longer usable. On a UDP-datagram socket 
				// this error indicates a previous send operation resulted in an ICMP Port Unreachable message.
				// Note the spocket settings on creation of the server. It makes us ignore these resets.
				IPEndPoint senderEndpoint = new IPEndPoint(0, 0);
				Byte[] receiveBytes = listener.EndReceive(ar, ref senderEndpoint);

				byte msgId = receiveBytes[0];

				if (msgId <= (byte) DefaultMessageIdTypes.ID_USER_PACKET_ENUM)
				{
					DefaultMessageIdTypes msgIdType = (DefaultMessageIdTypes) msgId;

					TraceReceive(msgIdType, msgId, receiveBytes, receiveBytes.Length);

					Package message = PackageFactory.CreatePackage(msgId, receiveBytes);

					switch (msgIdType)
					{
						case DefaultMessageIdTypes.ID_UNCONNECTED_PING:
						case DefaultMessageIdTypes.ID_UNCONNECTED_PING_OPEN_CONNECTIONS:
						{
							UnconnectedPing incoming = (UnconnectedPing) message;

							//TODO: This needs to be verified with RakNet first
							//response.sendpingtime = msg.sendpingtime;
							//response.sendpongtime = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

							var packet = new UnconnectedPong
							{
								serverId = 12345,
								pingId = incoming.pingId,
								serverName = "MCCPP;Demo;MiNET - Another MC server"
							};
							var data = packet.Encode();
							SendThroughQueue(data, senderEndpoint);
							break;
						}
						case DefaultMessageIdTypes.ID_OPEN_CONNECTION_REQUEST_1:
						{
							OpenConnectionRequest1 incoming = (OpenConnectionRequest1) message;

							var packet = new OpenConnectionReply1
							{
								serverGuid = 12345,
								mtuSize = incoming.mtuSize,
								serverHasSecurity = 0
							};

							var data = packet.Encode();
							SendThroughQueue(data, senderEndpoint);
							break;
						}
						case DefaultMessageIdTypes.ID_OPEN_CONNECTION_REQUEST_2:
						{
							OpenConnectionRequest2 incoming = (OpenConnectionRequest2) message;

							var packet = new OpenConnectionReply2
							{
								serverGuid = 12345,
								clientUdpPort = (short) senderEndpoint.Port,
								mtuSize = incoming.mtuSize,
								doSecurity = 0
							};

							var data = packet.Encode();
							SendThroughQueue(data, senderEndpoint);
							break;
						}
					}
				}
				else
				{
					DatagramHeader header = new DatagramHeader(receiveBytes[0]);
					if (!header.isACK && !header.isNAK && header.isValid)
					{
						if (receiveBytes[0] == 0xa0)
						{
							throw new Exception("Receive ERROR, NAK in wrong place");
						}

						var package = new ConnectedPackage();
						package.Decode(receiveBytes);
						var message = package.Message;

						SendAck(listener, senderEndpoint, package._sequenceNumber);

						TraceReceive((DefaultMessageIdTypes) message.Id, message.Id, receiveBytes, package.MessageLength);

						DoPlayerStuff(message, senderEndpoint);
					}
					else if (header.isACK && header.isValid)
					{
						//Debug.WriteLine("....... ACK .....");
					}
					else if (header.isNAK && header.isValid)
					{
						Debug.WriteLine("!!!! WARNING, NAK !!!!!");
					}
					else if (!header.isValid)
					{
						Debug.WriteLine("!!!! ERROR, Invalid header !!!!!");
					}
				}


				if (receiveBytes.Length != 0)
				{
					listener.BeginReceive(ReceiveCallback, listener);
				}
			}
			catch (Exception e)
			{
				Debug.Write(e);
			}
		}

		private void DoPlayerStuff(Package message, IPEndPoint senderEndpoint)
		{
			if (typeof (UnknownPackage) == message.GetType())
			{
				var msg = (UnknownPackage) message;
				TraceReceive((DefaultMessageIdTypes) msg.Id, msg.Id, msg.Message, msg.Message.Length, true);
			}

			if (typeof (ConnectedPing) == message.GetType())
			{
				var msg = (ConnectedPing) message;

				var response = new ConnectedPong();
				response.sendpingtime = msg.sendpingtime;
				response.sendpongtime = DateTimeOffset.UtcNow.Ticks/TimeSpan.TicksPerMillisecond;

				SendPackage(senderEndpoint, response);

				return;
			}

			if (typeof (ConnectionRequest) == message.GetType())
			{
				var msg = (ConnectionRequest) message;
				var response = new ConnectionRequestAcceptedManual((short) senderEndpoint.Port, msg.timestamp);
				response.Encode();

				SendPackage(senderEndpoint, response);

				_playerEndpoints.Remove(senderEndpoint);
				_playerEndpoints.Add(senderEndpoint, new Player(this, senderEndpoint, _level));

				return;
			}

			if (_playerEndpoints.ContainsKey(senderEndpoint))
			{
				if (_playerEndpoints[senderEndpoint].HandlePackage(message)) return;
			}
		}

		public void SendPackage(IPEndPoint senderEndpoint, Package message, Reliability reliability = Reliability.RELIABLE)
		{
			ConnectedPackage package = new ConnectedPackage();
			package.Message = message;
			package._reliability = reliability;
			package._reliableMessageNumber = _reliableMessageNumber++;
			package._sequenceNumber = _sequenceNumber++;

			byte[] data = package.Encode();

			TraceSend(message, data, package);

			SendThroughQueue(data, senderEndpoint);
		}

		private void SendAck(UdpClient listener, IPEndPoint senderEndpoint, Int24 sequenceNumber)
		{
			var ack = new Ack
			{
				sequenceNumber = sequenceNumber,
				count = 1,
				onlyOneSequence = 1
			};

			SendDirect(listener, ack.Encode(), senderEndpoint);
		}


		private void SendTimerElapsed(object sender, ElapsedEventArgs e)
		{
			lock (sendQueue)
			{
				if (sendQueue.Count == 0) return;

				Tuple<IPEndPoint, byte[]> item = sendQueue.Dequeue();

				SendDirect(_listener, item.Item2, item.Item1);
			}
		}

		private void SendThroughQueue(byte[] data, IPEndPoint senderEndpoint)
		{
			lock (sendQueue)
			{
				sendQueue.Enqueue(new Tuple<IPEndPoint, byte[]>(senderEndpoint, data));
			}
		}

		private void SendDirect(UdpClient listener, byte[] data, IPEndPoint senderEndpoint)
		{
			listener.Send(data, data.Length, senderEndpoint);
			listener.BeginSend(data, data.Length, senderEndpoint, SendDirectDone, listener);
		}

		private void SendDirectDone(IAsyncResult ar)
		{
			UdpClient listener = (UdpClient) ar.AsyncState;
			listener.EndSend(ar);
		}


		public static string ByteArrayToString(byte[] ba)
		{
			StringBuilder hex = new StringBuilder((ba.Length*2) + 100);
			hex.Append("{");
			foreach (byte b in ba)
				hex.AppendFormat("0x{0:x2},", b);
			hex.Append("}");
			return hex.ToString();
		}

		private static void TraceReceive(DefaultMessageIdTypes msgIdType, int msgId, byte[] receiveBytes, int length, bool isUnknown = false)
		{
			if (msgIdType != DefaultMessageIdTypes.ID_CONNECTED_PING && msgIdType != DefaultMessageIdTypes.ID_UNCONNECTED_PING)
			{
				Debug.Print("> Receive {2}: {1} (0x{0:x2})", msgId, msgIdType, isUnknown ? "Unknown" : "");
				Debug.Print("\tData: Length={1} {0}", ByteArrayToString(receiveBytes), length);
			}
		}

		private static void TraceSend(Package message, byte[] data, ConnectedPackage package)
		{
			if (message.Id != (decimal) DefaultMessageIdTypes.ID_CONNECTED_PONG && message.Id != (decimal) DefaultMessageIdTypes.ID_UNCONNECTED_PONG)
			{
				Debug.Print("< Send: {0:x2} {1} (0x{2:x2})", data[0], (DefaultMessageIdTypes) message.Id, message.Id);
				Debug.Print("\tData: Length={1} {0}", ByteArrayToString(data), package.MessageLength);
			}
		}
	}
}