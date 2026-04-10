using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Vendomat.Common.SSP
{
    public class SSPComms
    {
        public const byte ssp_CMD_SYNC = 0x11;
        public const byte SSP_STX = 0x7F;
        public const byte SSP_STEX = 0x7E;
        public const ushort CRC_SSP_SEED = 0xFFFF;
        public const ushort CRC_SSP_POLY = 0x8005;
        public const UInt64 MAX_RANDOM_INTEGER = 2147483648;
        System.Exception lastException = null;
        bool crcRetry = false;
        byte numCrcRetries = 0;
        SerialPort comPort;
        Stopwatch sWatch = new Stopwatch();
        SSP_TX_RX_PACKET ssp = new SSP_TX_RX_PACKET();
        RandomNumber rand = new RandomNumber();
        RNGCryptoServiceProvider rngCsp = new RNGCryptoServiceProvider();

        public bool OpenSSPComPort(SSP_COMMAND cmd)
        {
            try
            {
                // comPort = new SerialPort();
                comPort.DataReceived += new SerialDataReceivedEventHandler(comPort_DataReceived);
                comPort.PortName = cmd.ComPort;
                comPort.BaudRate = cmd.BaudRate;
                comPort.Parity = Parity.None;
                comPort.StopBits = StopBits.Two;
                comPort.DataBits = 8;
                comPort.Handshake = Handshake.None;
                comPort.WriteTimeout = 500;
                comPort.ReadTimeout = 500;
                comPort.Open();
            }
            catch (Exception ex)
            {
                lastException = ex;
                return false;
            }

            return true;
        }
        public bool OpenSSPComPort(SSP_COMMAND cmd, SerialPort serialCom)
        {
            try
            {
               if(comPort!=null)
                {
                    if(comPort.IsOpen)
                        CloseComPort();
                }
                comPort = serialCom;
                comPort.DataReceived += new SerialDataReceivedEventHandler(comPort_DataReceived);
                comPort.Parity = Parity.None;
                comPort.StopBits = StopBits.Two;
                comPort.DataBits = 8;
                comPort.Handshake = Handshake.None;
                comPort.WriteTimeout = 500;
                comPort.ReadTimeout = 500;
                comPort.ErrorReceived += ComPort_ErrorReceived;
                comPort.Open();
            }
            catch (Exception ex)
            {
                lastException = ex;
                return false;
            }

            return true;
        }

        private void ComPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {            
        }

        public bool CloseComPort()
        {
            try
            {
                
                comPort.Close();
                comPort.Dispose();
            }
            catch (Exception ex)
            {
                lastException = ex;
                return false;
            }

            return true;
        }

        public bool SSPSendCommand(SSP_COMMAND cmd, SSP_COMMAND_INFO sspInfo)
        {
            try
            {

                //clock_t txTime,currentTime,rxTime;
                int i;
                byte encryptLength;
                ushort crcR;
                byte[] tData = new byte[255];
                byte retry;
                UInt32 slaveCount;
                byte[] backupData = null;
                byte backupDataLength = 0;

                if (crcRetry)
                {
                    backupData = new byte[255];
                    cmd.CommandData.CopyTo(backupData, 0);
                    backupDataLength = cmd.CommandDataLength;
                }

                /* compile the SSP packet and check for errors  */
                if (!CompileSSPCommand(ref cmd, ref sspInfo))
                {
                    cmd.ResponseStatus = PORT_STATUS.SSP_PACKET_ERROR;
                    return false;
                }

                retry = cmd.RetryLevel;
                /* transmit the packet    */
                do
                {
                    ssp.NewResponse = false;  /* set flag to wait for a new reply from slave   */
                    ssp.rxBufferLength = 0;
                    ssp.rxPtr = 0;
                    if (WritePort() != true)
                    {
                        cmd.ResponseStatus = PORT_STATUS.PORT_ERROR;
                        return false;
                    }

                    /* wait for out reply   */
                    cmd.ResponseStatus = PORT_STATUS.SSP_REPLY_OK;
                    //txTime = clock();
                    sWatch.Stop();
                    sWatch.Reset();
                    sWatch.Start();
                    while (!ssp.NewResponse)
                    {
                        /* check for reply timeout   */
                        //currentTime = clock();
                        //if(currentTime - txTime > cmd.Timeout){
                        System.Threading.Thread.Sleep(1);
                        if (sWatch.ElapsedMilliseconds > cmd.Timeout)
                        {
                            sWatch.Stop();
                            sWatch.Reset();
                            cmd.ResponseStatus = PORT_STATUS.SSP_CMD_TIMEOUT;
                            break;
                        }
                    }

                    if (cmd.ResponseStatus == PORT_STATUS.SSP_REPLY_OK)
                        break;

                    retry--;
                } while (retry > 0);


                //rxTime = clock();
                //sspInfo.Receive.packetTime = (ushort)(rxTime - txTime);

                if (cmd.ResponseStatus == PORT_STATUS.SSP_CMD_TIMEOUT)
                {
                    sspInfo.Receive.PacketLength = 0;
                    return false;
                }

                sspInfo.Receive.PacketLength = (byte)(ssp.rxData[2] + 5);
                for (i = 0; i < sspInfo.Receive.PacketLength; i++)
                    sspInfo.Receive.PacketData[i] = ssp.rxData[i];

                /* load the command structure with ssp packet data   */
                if (ssp.rxData[3] == SSP_STEX)
                {   /* check for encrpted packet    */
                    encryptLength = (byte)(ssp.rxData[2] - 1);
                    //decrypt packet
                    aes_decrypt(ref cmd.Key, ref ssp.rxData, ref encryptLength, 4);
                    /* check the checksum    */
                    crcR = cal_crc_loop_CCITT_A((ushort)(encryptLength - 2), 4, ref ssp.rxData, CRC_SSP_SEED, CRC_SSP_POLY);
                    if ((byte)(crcR & 0xFF) != ssp.rxData[ssp.rxData[2] + 1] || (byte)((crcR >> 8) & 0xFF) != ssp.rxData[ssp.rxData[2] + 2])
                    {
                        if (crcRetry && numCrcRetries < 3)
                        {
                            backupData.CopyTo(cmd.CommandData, 0);
                            cmd.CommandDataLength = backupDataLength;
                            ++numCrcRetries;
                            --cmd.encPktCount;
                            if (SSPSendCommand(cmd, sspInfo))
                            {
                                numCrcRetries = 0;
                                return true;
                            }
                            else
                            {
                                cmd.ResponseStatus = PORT_STATUS.SSP_PACKET_ERROR_CRC_FAIL;
                                sspInfo.Receive.PacketLength = 0;
                                numCrcRetries = 0;
                                return false;
                            }
                        }
                        else
                        {
                            cmd.ResponseStatus = PORT_STATUS.SSP_PACKET_ERROR_CRC_FAIL;
                            sspInfo.Receive.PacketLength = 0;
                            numCrcRetries = 0;
                            return false;
                        }
                    }
                    /* check the slave count against the host count  */
                    slaveCount = 0;
                    for (i = 0; i < 4; i++)
                        slaveCount += (UInt32)(ssp.rxData[5 + i]) << (i * 8);
                    /* no match then we discard this packet and do not act on it's info  */
                    if (slaveCount != cmd.encPktCount)
                    {
                        cmd.ResponseStatus = PORT_STATUS.SSP_PACKET_ERROR_ENC_COUNT;
                        sspInfo.Receive.PacketLength = 0;
                        return false;
                    }

                    /* restore data for correct decode  */
                    ssp.rxBufferLength = (byte)(ssp.rxData[4] + 5);
                    tData[0] = ssp.rxData[0];
                    tData[1] = ssp.rxData[1];
                    tData[2] = ssp.rxData[4];
                    for (i = 0; i < ssp.rxData[4]; i++)
                        tData[3 + i] = ssp.rxData[9 + i];
                    crcR = cal_crc_loop_CCITT_A((ushort)(ssp.rxBufferLength - 3), 1, ref tData, CRC_SSP_SEED, CRC_SSP_POLY);
                    tData[3 + ssp.rxData[4]] = (byte)(crcR & 0xFF);
                    tData[4 + ssp.rxData[4]] = (byte)((crcR >> 8) & 0xFF);
                    for (i = 0; i < ssp.rxBufferLength; i++)
                        ssp.rxData[i] = tData[i];
                }

                cmd.ResponseDataLength = ssp.rxData[2];
                for (i = 0; i < cmd.ResponseDataLength; i++)
                    cmd.ResponseData[i] = ssp.rxData[i + 3];

                sspInfo.PreEncryptedRecieve.PacketLength = ssp.rxBufferLength;
                for (i = 0; i < ssp.rxBufferLength; i++)
                    sspInfo.PreEncryptedRecieve.PacketData[i] = ssp.rxData[i];

                /* alternate the seq bit   */
                if (cmd.sspSeq == 0x80)
                    cmd.sspSeq = 0;
                else
                    cmd.sspSeq = 0x80;


                /* terminate the function   */
                cmd.ResponseStatus = PORT_STATUS.SSP_REPLY_OK;
                return true;

            }
            catch (Exception ex)
            {

                ;
            }
            return false;
        }

        bool CompileSSPCommand(ref SSP_COMMAND cmd, ref SSP_COMMAND_INFO sspInfo)
        {

            UInt32 i, j;
            ushort crc;
            byte[] tBuffer = new byte[255];

            //clear the receive buffer
            ssp.rxPtr = 0;
            for (i = 0; i < 255; i++)
                ssp.rxData[i] = 0x00;

            /* for sync commands reset the deq bit   */
            if (cmd.CommandData[0] == ssp_CMD_SYNC)
                cmd.sspSeq = 0x80;

            /* update the log packet data before any encryption   */
            sspInfo.Encrypted = cmd.EncryptionStatus;
            sspInfo.PreEncryptedTransmit.PacketLength = (byte)(cmd.CommandDataLength + 5);
            sspInfo.PreEncryptedTransmit.PacketData[0] = SSP_STX;                   /* ssp packet start   */
            sspInfo.PreEncryptedTransmit.PacketData[1] = (byte)(cmd.SSPAddress | cmd.sspSeq);  /* the address/seq bit */
            sspInfo.PreEncryptedTransmit.PacketData[2] = cmd.CommandDataLength;    /* the data length only (always > 0)  */
            for (i = 0; i < cmd.CommandDataLength; i++)  /* add the command data  */
                sspInfo.PreEncryptedTransmit.PacketData[3 + i] = cmd.CommandData[i];
            /* calc the packet CRC  (all bytes except STX)   */
            crc = cal_crc_loop_CCITT_A((ushort)(cmd.CommandDataLength + 2), 1, ref sspInfo.PreEncryptedTransmit.PacketData, CRC_SSP_SEED, CRC_SSP_POLY);
            sspInfo.PreEncryptedTransmit.PacketData[3 + cmd.CommandDataLength] = (byte)(crc & 0xFF);
            sspInfo.PreEncryptedTransmit.PacketData[4 + cmd.CommandDataLength] = (byte)((crc >> 8) & 0xFF);

            /* is this a encrypted packet  */
            if (cmd.EncryptionStatus)
            {

                if (!EncryptSSPPacket(ref cmd.encPktCount, ref cmd.CommandData, ref cmd.CommandData, ref cmd.CommandDataLength, ref cmd.CommandDataLength, ref cmd.Key))
                    return false;

            }

            /* create the packet from this data   */
            ssp.CheckStuff = 0;
            ssp.SSPAddress = cmd.SSPAddress;
            ssp.rxPtr = 0;
            ssp.txPtr = 0;
            ssp.txBufferLength = (byte)(cmd.CommandDataLength + 5);  /* the full ssp packet length   */
            ssp.txData[0] = SSP_STX;                    /* ssp packet start   */
            ssp.txData[1] = (byte)(cmd.SSPAddress | cmd.sspSeq);  /* the address/seq bit */
            ssp.txData[2] = cmd.CommandDataLength;    /* the data length only (always > 0)  */
            for (i = 0; i < cmd.CommandDataLength; i++)  /* add the command data  */
                ssp.txData[3 + i] = cmd.CommandData[i];
            /* calc the packet CRC  (all bytes except STX)   */
            crc = cal_crc_loop_CCITT_A((ushort)(ssp.txBufferLength - 3), 1, ref ssp.txData, CRC_SSP_SEED, CRC_SSP_POLY);
            ssp.txData[3 + cmd.CommandDataLength] = (byte)(crc & 0xFF);
            ssp.txData[4 + cmd.CommandDataLength] = (byte)((crc >> 8) & 0xFF);

            for (i = 0; i < ssp.txBufferLength; i++)
                sspInfo.Transmit.PacketData[i] = ssp.txData[i];
            sspInfo.Transmit.PacketLength = ssp.txBufferLength;


            /* we now need to 'byte stuff' this buffered data   */
            j = 0;
            tBuffer[j++] = ssp.txData[0];
            for (i = 1; i < ssp.txBufferLength; i++)
            {
                tBuffer[j] = ssp.txData[i];
                if (ssp.txData[i] == SSP_STX)
                {
                    tBuffer[++j] = SSP_STX;   /* SSP_STX found in data so add another to 'stuff it'  */
                }
                j++;
            }
            for (i = 0; i < j; i++)
                ssp.txData[i] = tBuffer[i];
            ssp.txBufferLength = (byte)j;

            return true;
        }

        ushort cal_crc_loop_CCITT_A(ushort l, ushort offset, ref byte[] p, ushort seed, ushort cd)
        {
            ushort i, j;
            ushort crc = seed;

            for (i = 0; i < l; ++i)
            {
                crc ^= (ushort)(p[i + offset] << 8);
                for (j = 0; j < 8; ++j)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ cd);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }

        bool WritePort()
        {
            try
            {
                comPort.Write(ssp.txData, 0, ssp.txBufferLength);
            }
            catch (Exception ex)
            {
                lastException = ex;
                return false;
            }
            return true;
        }

        void comPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort sp = (SerialPort)sender;

            if (sp.IsOpen == false) return;
            try
            {
                while (sp.BytesToRead > 0)
                {
                    SSPDataIn((byte)sp.ReadByte());
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                return;
            }
        }

        void SSPDataIn(byte RxChar)
        {
            ushort crc;

            if (RxChar == SSP_STX && ssp.rxPtr == 0)
            {
                // packet start
                ssp.rxData[ssp.rxPtr++] = RxChar;
            }
            else
            {
                // if last byte was start byte, and next is not then
                // restart the packet
                if (ssp.CheckStuff == 1)
                {
                    if (RxChar != SSP_STX)
                    {
                        ssp.rxData[0] = SSP_STX;
                        ssp.rxData[1] = RxChar;
                        ssp.rxPtr = 2;
                    }
                    else
                        ssp.rxData[ssp.rxPtr++] = RxChar;
                    // reset stuff check flag	
                    ssp.CheckStuff = 0;
                }
                else
                {
                    // set flag for stuffed byte check
                    if (RxChar == SSP_STX)
                        ssp.CheckStuff = 1;
                    else
                    {
                        // add data to packet
                        ssp.rxData[ssp.rxPtr++] = RxChar;
                        // get the packet length
                        if (ssp.rxPtr == 3)
                            ssp.rxBufferLength = (byte)(ssp.rxData[2] + 5);
                    }
                }
                // are we at the end of the packet
                if (ssp.rxPtr == ssp.rxBufferLength)
                {
                    // is this packet for us ??
                    if ((ssp.rxData[1] & SSP_STX) == ssp.SSPAddress)
                    {
                        // is the checksum correct
                        crc = cal_crc_loop_CCITT_A((byte)(ssp.rxBufferLength - 3), 1, ref ssp.rxData, CRC_SSP_SEED, CRC_SSP_POLY);
                        if ((byte)(crc & 0xFF) == ssp.rxData[ssp.rxBufferLength - 2] && (byte)((crc >> 8) & 0xFF) == ssp.rxData[ssp.rxBufferLength - 1])
                            ssp.NewResponse = true;  /* we have a new response so set flag  */
                    }
                    // reset packet 
                    ssp.rxPtr = 0;
                    ssp.CheckStuff = 0;
                }
            }
        }

        bool EncryptSSPPacket(ref UInt32 ePktCount, ref byte[] dataIn, ref byte[] dataOut, ref byte lengthIn, ref byte lengthOut, ref SSP_FULL_KEY key)
        {
            const byte FIXED_PACKET_LENGTH = 7;
            const byte C_MAX_KEY_LENGTH = 16;
            byte pkLength, i, packLength = 0;
            ushort crc;
            byte[] tmpData = new byte[255];

            pkLength = (byte)(lengthIn + FIXED_PACKET_LENGTH);

            /* find the length of packing data required */
            if (pkLength % C_MAX_KEY_LENGTH != 0)
            {
                packLength = (byte)(C_MAX_KEY_LENGTH - (pkLength % C_MAX_KEY_LENGTH));
            }
            pkLength += packLength;

            tmpData[0] = lengthIn; /* the length of the data without packing */

            /* add in the encrypted packet count   */
            for (i = 0; i < 4; i++)
                tmpData[1 + i] = (byte)((ePktCount >> (8 * i) & 0xFF));


            for (i = 0; i < lengthIn; i++)
                tmpData[i + 5] = dataIn[i];

            /* add random packing data  */
            for (i = 0; i < packLength; i++)
            {
                tmpData[5 + lengthIn + i] = (byte)rand.GenerateRandomNumber();
            }
            /* add CRC to packet end   */

            crc = cal_crc_loop_CCITT_A((ushort)(pkLength - 2), 0, ref tmpData, CRC_SSP_SEED, CRC_SSP_POLY);

            tmpData[pkLength - 2] = (byte)(crc & 0xFF);
            tmpData[pkLength - 1] = (byte)((crc >> 8) & 0xFF);

            aes_encrypt(ref key, ref tmpData, ref pkLength, 0);

            pkLength++; /* increment as the final length will have an STEX command added   */
            lengthOut = pkLength;
            dataOut[0] = SSP_STEX;
            for (i = 0; i < pkLength - 1; i++)
            {
                dataOut[1 + i] = tmpData[i];
            }

            ePktCount++;  /* increment the counter after a successful encrypted packet   */

            return true;
        }

        void aes_encrypt(ref SSP_FULL_KEY sspKey, ref byte[] data, ref byte length, byte offset)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (Aes aes = new AesManaged())
                {

                    //byte[] tmp = new byte[length];
                    byte[] key = new byte[16];
                    byte i;

                    for (i = 0; i < 8; i++)
                    {
                        key[i] = (byte)(sspKey.FixedKey >> (8 * i));
                        key[i + 8] = (byte)(sspKey.VariableKey >> (8 * i));
                    }

                    aes.BlockSize = 128;
                    //aes.FeedbackSize = 128;
                    aes.KeySize = 128;
                    aes.Key = key;
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;

                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, offset, length);
                    }
                    data = ms.ToArray();
                }
            }
        }

        void aes_decrypt(ref SSP_FULL_KEY sspKey, ref byte[] data, ref byte length, byte offset)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (Aes aes = new AesManaged())
                {

                    byte[] tmp = new byte[length];
                    byte[] key = new byte[16];
                    byte i;

                    for (i = 0; i < 8; i++)
                    {
                        key[i] = (byte)(sspKey.FixedKey >> (8 * i));
                        key[i + 8] = (byte)(sspKey.VariableKey >> (8 * i));
                    }

                    aes.BlockSize = 128;
                    //aes.FeedbackSize = 128;
                    aes.KeySize = 128;
                    aes.Key = key;
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;

                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, offset, length);
                    }
                    tmp = ms.ToArray();
                    for (i = 0; i < length; i++)
                    {
                        data[i + offset] = tmp[i];
                    }
                }
            }
        }

        public bool InitiateSSPHostKeys(SSP_KEYS keys, SSP_COMMAND cmd)
        {
            UInt64 swap = 0;

            /* create the two random prime numbers  */
            keys.Generator = rand.GeneratePrime();
            keys.Modulus = rand.GeneratePrime();
            /* make sure Generator is larger than Modulus   */
            if (keys.Generator < keys.Modulus)
            {
                swap = keys.Generator;
                keys.Generator = keys.Modulus;
                keys.Modulus = swap;
            }


            if (CreateHostInterKey(keys) == false)
                return false;

            // reset the packet counter here for a successful key neg 
            cmd.encPktCount = 0;

            return true;
        }

        // Creates a host intermediate key 
        public bool CreateHostInterKey(SSP_KEYS keys)
        {
            if (keys.Generator == 0 || keys.Modulus == 0)
                return false;

            keys.HostRandom = (UInt64)(rand.GenerateRandomNumber() % MAX_RANDOM_INTEGER);
            keys.HostInter = rand.XpowYmodN(keys.Generator, keys.HostRandom, keys.Modulus);

            return true;
        }

        // creates the host encryption key   
        public bool CreateSSPHostEncryptionKey(SSP_KEYS keys)
        {
            keys.KeyHost = rand.XpowYmodN(keys.SlaveInterKey, keys.HostRandom, keys.Modulus);

            return true;
        }

        // returns the last exception caught
        public System.Exception GetLastException()
        {
            return lastException;
        }

        // start or stop retrying on a CRC fail
        public void RetryOnFailedCRC(bool doRetry)
        {
            crcRetry = doRetry;
        }
    }
}
