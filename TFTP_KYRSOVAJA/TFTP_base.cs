using System.Text;

namespace TFTP_KYRSOVAJA
{
    class TftpPacket
    {
        //Все пакеты tftp протокола
        public enum Opcode : ushort
        {
            ReadRequest = 1,
            WriteRequest = 2,
            Data = 3,
            Ack = 4,
            Error = 5
        }

        //коды ошибок tftp протокола
        public enum ErrorCode : ushort
        {
            Undefined = 0,
            FileNotFound = 1,
            AccessViolation = 2,
            DiskFull = 3,
            IllegalOperation = 4,
            UnknownTransferId = 5,
            FileAlreadyExists = 6,
            NoSuchUser = 7
        }

        //Создание запроса на чтение
        public static byte[] CreateReadRequestPacket(string filename, string mode)
        {
            return CreateRequestPacket(Opcode.ReadRequest, filename, mode);
        }

        //Создание запроса на запись 
        public static byte[] CreateWriteRequestPacket(string filename, string mode)
        {
            return CreateRequestPacket(Opcode.WriteRequest, filename, mode);
        }

        //Создание базового пакета
        private static byte[] CreateRequestPacket(Opcode opcode, string filename, string mode)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes((ushort)opcode).Reverse());
            bytes.AddRange(Encoding.ASCII.GetBytes(filename));
            bytes.Add(0); // null terminator
            bytes.AddRange(Encoding.ASCII.GetBytes(mode));
            bytes.Add(0); // null terminator
            return bytes.ToArray();
        }

        public static byte[] CreateDataPacket(ushort blockNumber, byte[] data)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes((ushort)Opcode.Data).Reverse());
            bytes.AddRange(BitConverter.GetBytes(blockNumber).Reverse());
            bytes.AddRange(data);
            return bytes.ToArray();
        }

        public static byte[] CreateAckPacket(ushort blockNumber)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes((ushort)Opcode.Ack).Reverse());
            bytes.AddRange(BitConverter.GetBytes(blockNumber).Reverse());
            return bytes.ToArray();
        }

        public static byte[] CreateErrorPacket(ErrorCode errorCode, string errorMessage)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes((ushort)Opcode.Error).Reverse());
            bytes.AddRange(BitConverter.GetBytes((ushort)errorCode).Reverse());
            bytes.AddRange(Encoding.ASCII.GetBytes(errorMessage));
            bytes.Add(0); // null terminator
            return bytes.ToArray();
        }

        public static (Opcode opcode, byte[] data) ParsePacket(byte[] packet)
        {
            if (packet.Length < 2)
                throw new ArgumentException("Invalid TFTP packet");

            var opcode = (Opcode)BitConverter.ToUInt16(packet.Take(2).Reverse().ToArray(), 0);

            switch (opcode)
            {
                case Opcode.ReadRequest:
                case Opcode.WriteRequest:
                    return (opcode, packet.Skip(2).ToArray());
                case Opcode.Data:
                    return (opcode, packet.Skip(2).ToArray());
                case Opcode.Ack:
                    return (opcode, packet.Skip(2).Take(2).ToArray());
                case Opcode.Error:
                    return (opcode, packet.Skip(2).ToArray());
                default:
                    throw new ArgumentException("Unknown TFTP opcode");
            }
        }
    }
}
