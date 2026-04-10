using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vendomat.Common.SSP
{
    public class SSP_KEYS
    {
        public UInt64 Generator;
        public UInt64 Modulus;
        public UInt64 HostInter;
        public UInt64 HostRandom;
        public UInt64 SlaveInterKey;
        public UInt64 SlaveRandom;
        public UInt64 KeyHost;
        public UInt64 KeySlave;
    };

    public class SSP_FULL_KEY
    {
        public UInt64 FixedKey;
        public UInt64 VariableKey;
    };

    public class SSP_COMMAND
    {
        public SSP_FULL_KEY Key = new SSP_FULL_KEY();
        public Int32 BaudRate = 9600;
        public UInt32 Timeout = 3000;
        public string ComPort;
        public byte SSPAddress = 0;
        public byte RetryLevel = 3;
        public bool EncryptionStatus = false;
        public byte CommandDataLength;
        public byte[] CommandData = new byte[255];
        public PORT_STATUS ResponseStatus = new PORT_STATUS();
        public byte ResponseDataLength;
        public byte[] ResponseData = new byte[255];
        public UInt32 encPktCount;
        public byte sspSeq;
    };

    public class SSP_PACKET
    {
        public ushort packetTime;
        public byte PacketLength;
        public byte[] PacketData = new byte[255];
    };


    public class SSP_COMMAND_INFO
    {
        public bool Encrypted;
        public SSP_PACKET Transmit = new SSP_PACKET();
        public SSP_PACKET Receive = new SSP_PACKET();
        public SSP_PACKET PreEncryptedTransmit = new SSP_PACKET();
        public SSP_PACKET PreEncryptedRecieve = new SSP_PACKET();
    };

    class SSP_TX_RX_PACKET
    {
        public byte txPtr;
        public byte[] txData = new byte[255];
        public byte rxPtr;
        public byte[] rxData = new byte[255];
        public byte txBufferLength;
        public byte rxBufferLength;
        public byte SSPAddress;
        public bool NewResponse;
        public byte CheckStuff;
    };

    public enum PORT_STATUS
    {
        PORT_CLOSED,
        PORT_OPEN,
        PORT_ERROR,
        SSP_REPLY_OK,
        SSP_PACKET_ERROR,
        SSP_CMD_TIMEOUT,
        SSP_PACKET_ERROR_CRC_FAIL,
        SSP_PACKET_ERROR_ENC_COUNT,
    };
}
