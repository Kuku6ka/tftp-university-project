using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading.Tasks;

namespace TFTP_KYRSOVAJA
{
    class TftpServer
    {
        private const int DefaultTftpPort = 69;
        private const int BufferSize = 516; // 512 data + 4 header
        private const int DataSize = 512;

        private readonly string _rootDirectory;
        private UdpClient _udpServer;
        private bool _isRunning;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public event Action<string> LogMessage;

        public TftpServer(string rootDirectory)
        {
            _rootDirectory = rootDirectory;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public void Start()
        {
            _isRunning = true;
            _udpServer = new UdpClient(DefaultTftpPort);
            LogMessage?.Invoke($"TFTP Server started on port {DefaultTftpPort}");

            Task.Run(() => ListenForRequests(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }

        public void Stop()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();
            _udpServer.Close();
            LogMessage?.Invoke("TFTP Server stopped");
        }

        private async Task ListenForRequests(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpServer.ReceiveAsync();
                    var remoteEndPoint = result.RemoteEndPoint;
                    var packet = result.Buffer;

                    LogMessage?.Invoke($"Received packet from {remoteEndPoint}");

                    // Обработка запроса в отдельном потоке
                    ThreadPool.QueueUserWorkItem(async state =>
                    {
                        try
                        {
                            await HandleRequestAsync(packet, remoteEndPoint);
                        }
                        catch (Exception ex)
                        {
                            LogMessage?.Invoke($"Error handling request: {ex.Message}");
                        }
                    });
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                {
                    // Server is stopping
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Error receiving packet: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(byte[] packet, IPEndPoint remoteEndPoint)
        {
            var (opcode, data) = TftpPacket.ParsePacket(packet);

            using (var transferSocket = new UdpClient(0))
            {
                var transferEndPoint = new IPEndPoint(remoteEndPoint.Address, remoteEndPoint.Port);

                try
                {
                    switch (opcode)
                    {
                        case TftpPacket.Opcode.ReadRequest:
                            await HandleReadRequestAsync(data, transferSocket, transferEndPoint);
                            break;
                        case TftpPacket.Opcode.WriteRequest:
                            await HandleWriteRequestAsync(data, transferSocket, transferEndPoint);
                            break;
                        default:
                            throw new InvalidOperationException($"Unexpected opcode: {opcode}");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Transfer error: {ex.Message}");
                    var errorPacket = TftpPacket.CreateErrorPacket(TftpPacket.ErrorCode.IllegalOperation, ex.Message);
                    await transferSocket.SendAsync(errorPacket, errorPacket.Length, transferEndPoint);
                }
            }
        }

        private async Task HandleReadRequestAsync(byte[] requestData, UdpClient transferSocket, IPEndPoint remoteEndPoint)
        {
            var parts = SplitNullTerminatedStrings(requestData);
            if (parts.Length < 2)
                throw new ArgumentException("Invalid RRQ packet");

            var filename = parts[0];
            var mode = parts[1].ToLower();

            if (mode != "octet" && mode != "netascii")
                throw new ArgumentException($"Unsupported mode: {mode}");

            var filePath = Path.Combine(_rootDirectory, filename);
            if (!File.Exists(filePath))
            {
                var errorPacket = TftpPacket.CreateErrorPacket(TftpPacket.ErrorCode.FileNotFound, "File not found");
                await transferSocket.SendAsync(errorPacket, errorPacket.Length, remoteEndPoint);
                return;
            }

            LogMessage?.Invoke($"Starting file transfer: {filename}");

            using (var fileStream = File.OpenRead(filePath))
            {
                ushort blockNumber = 1;
                var buffer = new byte[DataSize];
                int bytesRead;

                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, DataSize)) > 0)
                {
                    var dataPacket = TftpPacket.CreateDataPacket(blockNumber, buffer.Take(bytesRead).ToArray());
                    await transferSocket.SendAsync(dataPacket, dataPacket.Length, remoteEndPoint);

                    // Ждем ACK
                    var ackResult = await transferSocket.ReceiveAsync();
                    var (ackOpcode, ackData) = TftpPacket.ParsePacket(ackResult.Buffer);

                    if (ackOpcode != TftpPacket.Opcode.Ack)
                        throw new InvalidOperationException("Expected ACK packet");

                    var receivedBlockNumber = BitConverter.ToUInt16(ackData.Take(2).Reverse().ToArray(), 0);
                    if (receivedBlockNumber != blockNumber)
                        throw new InvalidOperationException($"Block number mismatch: expected {blockNumber}, got {receivedBlockNumber}");

                    blockNumber++;
                }
            }

            LogMessage?.Invoke($"File transfer completed: {filename}");
        }

        private async Task HandleWriteRequestAsync(byte[] requestData, UdpClient transferSocket, IPEndPoint remoteEndPoint)
        {
            var parts = SplitNullTerminatedStrings(requestData);
            if (parts.Length < 2)
                throw new ArgumentException("Invalid WRQ packet");

            var filename = parts[0];
            var mode = parts[1].ToLower();

            if (mode != "octet" && mode != "netascii")
                throw new ArgumentException($"Unsupported mode: {mode}");

            var filePath = Path.Combine(_rootDirectory, filename);
            if (File.Exists(filePath))
            {
                var errorPacket = TftpPacket.CreateErrorPacket(TftpPacket.ErrorCode.FileAlreadyExists, "File already exists");
                await transferSocket.SendAsync(errorPacket, errorPacket.Length, remoteEndPoint);
                return;
            }

            // Отправляем ACK с блоком 0
            var initialAck = TftpPacket.CreateAckPacket(0);
            await transferSocket.SendAsync(initialAck, initialAck.Length, remoteEndPoint);

            LogMessage?.Invoke($"Starting file reception: {filename}");

            using (var fileStream = File.Create(filePath))
            {
                ushort expectedBlockNumber = 1;
                var buffer = new byte[BufferSize];

                while (true)
                {
                    var dataResult = await transferSocket.ReceiveAsync();
                    var (dataOpcode, data) = TftpPacket.ParsePacket(dataResult.Buffer);

                    if (dataOpcode == TftpPacket.Opcode.Error)
                    {
                        var errorCode = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                        var errorMessage = Encoding.ASCII.GetString(data.Skip(2).TakeWhile(b => b != 0).ToArray());
                        throw new Exception($"Client error: {errorMessage} (code {errorCode})");
                    }

                    if (dataOpcode != TftpPacket.Opcode.Data)
                        throw new InvalidOperationException("Expected DATA packet");

                    var blockNumber = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                    if (blockNumber != expectedBlockNumber)
                    {
                        // Повторно отправляем последний ACK
                        var ackPacket = TftpPacket.CreateAckPacket((ushort)(expectedBlockNumber - 1));
                        await transferSocket.SendAsync(ackPacket, ackPacket.Length, remoteEndPoint);
                        continue;
                    }

                    var fileData = data.Skip(2).ToArray();
                    await fileStream.WriteAsync(fileData, 0, fileData.Length);

                    // Отправляем ACK
                    var ack = TftpPacket.CreateAckPacket(blockNumber);
                    await transferSocket.SendAsync(ack, ack.Length, remoteEndPoint);

                    // Если получен пакет меньше максимального размера - это последний пакет
                    if (fileData.Length < DataSize)
                        break;

                    expectedBlockNumber++;
                }
            }

            LogMessage?.Invoke($"File reception completed: {filename}");
        }

        private string[] SplitNullTerminatedStrings(byte[] data)
        {
            var parts = new List<string>();
            var currentPart = new List<byte>();

            foreach (var b in data)
            {
                if (b == 0)
                {
                    if (currentPart.Count > 0)
                    {
                        parts.Add(Encoding.ASCII.GetString(currentPart.ToArray()));
                        currentPart.Clear();
                    }
                }
                else
                {
                    currentPart.Add(b);
                }
            }

            return parts.ToArray();
        }
    }

}
