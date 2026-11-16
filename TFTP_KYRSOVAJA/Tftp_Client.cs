using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TFTP_KYRSOVAJA
{
    class TftpClient
    {
        private const int BufferSize = 516; // 512 data + 4 header
        private const int DataSize = 512;
        private const int DefaultTimeout = 5000;
        private const int MaxRetries = 5;

        public event Action<string> LogMessage;

        public async Task DownloadFileAsync(string serverIp, string remoteFilename, string localFilename)
        {
            LogMessage?.Invoke($"Starting download: {remoteFilename} -> {localFilename}");

            using (var client = new UdpClient())
            {
                var serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), 69);
                var rrqPacket = TftpPacket.CreateReadRequestPacket(remoteFilename, "octet");
                await client.SendAsync(rrqPacket, rrqPacket.Length, serverEndPoint);

                // Получаем первый пакет данных (сервер может ответить с другого порта)
                if (await ReceiveWithTimeoutAsync(client, DefaultTimeout) is not { } firstResponse)
                    throw new TimeoutException("No response from server");

                var transferEndPoint = firstResponse.RemoteEndPoint;
                var (opcode, data) = TftpPacket.ParsePacket(firstResponse.Buffer);

                if (opcode == TftpPacket.Opcode.Error)
                {
                    var errorCode = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                    var errorMessage = Encoding.ASCII.GetString(data.Skip(2).TakeWhile(b => b != 0).ToArray());
                    throw new Exception($"Server error: {errorMessage} (code {errorCode})");
                }

                if (opcode != TftpPacket.Opcode.Data)
                    throw new InvalidOperationException($"Unexpected opcode: {opcode}");

                ushort expectedBlockNumber = 1;
                var blockNumber = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                if (blockNumber != expectedBlockNumber)
                    throw new InvalidOperationException($"Block number mismatch: expected {expectedBlockNumber}, got {blockNumber}");

                using (var fileStream = File.Create(localFilename))
                {
                    var fileData = data.Skip(2).ToArray();
                    await fileStream.WriteAsync(fileData, 0, fileData.Length);

                    // Отправляем ACK
                    var ackPacket = TftpPacket.CreateAckPacket(blockNumber);
                    await client.SendAsync(ackPacket, ackPacket.Length, transferEndPoint);

                    // Если первый пакет уже был последним
                    if (fileData.Length < DataSize)
                    {
                        LogMessage?.Invoke("Download completed");
                        return;
                    }

                    expectedBlockNumber++;

                    // Получаем остальные пакеты
                    while (true)
                    {
                        if (await ReceiveWithTimeoutAsync(client, DefaultTimeout) is not { } response)
                            throw new TimeoutException("No response from server");

                        var (nextOpcode, nextData) = TftpPacket.ParsePacket(response.Buffer);

                        if (nextOpcode == TftpPacket.Opcode.Error)
                        {
                            var errorCode = BitConverter.ToUInt16(nextData.Take(2).Reverse().ToArray(), 0);
                            var errorMessage = Encoding.ASCII.GetString(nextData.Skip(2).TakeWhile(b => b != 0).ToArray());
                            throw new Exception($"Server error: {errorMessage} (code {errorCode})");
                        }

                        if (nextOpcode != TftpPacket.Opcode.Data)
                            throw new InvalidOperationException($"Unexpected opcode: {nextOpcode}");

                        blockNumber = BitConverter.ToUInt16(nextData.Take(2).Reverse().ToArray(), 0);
                        if (blockNumber != expectedBlockNumber)
                        {
                            // Повторно отправляем последний ACK
                            var lastAck = TftpPacket.CreateAckPacket((ushort)(expectedBlockNumber - 1));
                            await client.SendAsync(lastAck, lastAck.Length, transferEndPoint);
                            continue;
                        }

                        fileData = nextData.Skip(2).ToArray();
                        await fileStream.WriteAsync(fileData, 0, fileData.Length);

                        // Отправляем ACK
                        ackPacket = TftpPacket.CreateAckPacket(blockNumber);
                        await client.SendAsync(ackPacket, ackPacket.Length, transferEndPoint);

                        // Если получен пакет меньше максимального размера - это последний пакет
                        if (fileData.Length < DataSize)
                            break;

                        expectedBlockNumber++;
                    }
                }
            }

            LogMessage?.Invoke("Download completed");
        }

        public async Task UploadFileAsync(string serverIp, string localFilename, string remoteFilename)
        {
            LogMessage?.Invoke($"Starting upload: {localFilename} -> {remoteFilename}");

            using (var client = new UdpClient())
            {
                var serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), 69);
                var wrqPacket = TftpPacket.CreateWriteRequestPacket(remoteFilename, "octet");
                await client.SendAsync(wrqPacket, wrqPacket.Length, serverEndPoint);

                // Ожидаем первый ACK (блок 0) или ошибку
                if (await ReceiveWithTimeoutAsync(client, DefaultTimeout) is not { } firstResponse)
                    throw new TimeoutException("No response from server");

                var transferEndPoint = firstResponse.RemoteEndPoint;
                var (opcode, data) = TftpPacket.ParsePacket(firstResponse.Buffer);

                if (opcode == TftpPacket.Opcode.Error)
                {
                    var errorCode = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                    var errorMessage = Encoding.ASCII.GetString(data.Skip(2).TakeWhile(b => b != 0).ToArray());
                    throw new Exception($"Server error: {errorMessage} (code {errorCode})");
                }

                if (opcode != TftpPacket.Opcode.Ack)
                    throw new InvalidOperationException($"Unexpected opcode: {opcode}");

                var blockNumber = BitConverter.ToUInt16(data.Take(2).Reverse().ToArray(), 0);
                if (blockNumber != 0)
                    throw new InvalidOperationException($"Expected ACK for block 0, got {blockNumber}");

                using (var fileStream = File.OpenRead(localFilename))
                {
                    var buffer = new byte[DataSize];
                    ushort currentBlockNumber = 1;
                    int bytesRead;

                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, DataSize)) > 0)
                    {
                        var retryCount = 0;
                        bool success;

                        do
                        {
                            success = true;
                            var dataPacket = TftpPacket.CreateDataPacket(currentBlockNumber, buffer.Take(bytesRead).ToArray());
                            await client.SendAsync(dataPacket, dataPacket.Length, transferEndPoint);

                            // Ждем ACK

                            if (await ReceiveWithTimeoutAsync(client, DefaultTimeout) is not { } ackResponse)
                            {
                                retryCount++;
                                if (retryCount >= MaxRetries)
                                    throw new TimeoutException($"Max retries ({MaxRetries}) exceeded for block {currentBlockNumber}");

                                LogMessage?.Invoke($"Timeout waiting for ACK, retrying ({retryCount}/{MaxRetries})");
                                success = false;
                                continue;
                            }

                            var (ackOpcode, ackData) = TftpPacket.ParsePacket(ackResponse.Buffer);

                            if (ackOpcode == TftpPacket.Opcode.Error)
                            {
                                var errorCode = BitConverter.ToUInt16(ackData.Take(2).Reverse().ToArray(), 0);
                                var errorMessage = Encoding.ASCII.GetString(ackData.Skip(2).TakeWhile(b => b != 0).ToArray());
                                throw new Exception($"Server error: {errorMessage} (code {errorCode})");
                            }

                            if (ackOpcode != TftpPacket.Opcode.Ack)
                                throw new InvalidOperationException($"Unexpected opcode: {ackOpcode}");

                            var ackBlockNumber = BitConverter.ToUInt16(ackData.Take(2).Reverse().ToArray(), 0);
                            if (ackBlockNumber != currentBlockNumber)
                            {
                                LogMessage?.Invoke($"Received ACK for block {ackBlockNumber}, expected {currentBlockNumber}");
                                success = false;
                            }
                        } while (!success);

                        currentBlockNumber++;
                    }
                }
            }

            LogMessage?.Invoke("Upload completed");
        }

        private async Task<UdpReceiveResult?> ReceiveWithTimeoutAsync(UdpClient client, int timeout)
        {
            var receiveTask = client.ReceiveAsync();
            var timeoutTask = Task.Delay(timeout);

            var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
            if (completedTask == timeoutTask)
                return null;

            return await receiveTask;
        }
    }
}
