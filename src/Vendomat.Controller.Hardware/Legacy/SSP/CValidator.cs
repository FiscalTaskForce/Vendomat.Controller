using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vendomat.Common.SSP
{
    public delegate void NoteReceivedEventHandler(ValidatorCredit e);
    public delegate void ValidatorEvent(PollResponse Type, ValidatorCredit e);
    public class CValidator
    {
        // ssp library variables
        SSPComms m_eSSP;
        SSP_COMMAND m_cmd;
        SSP_KEYS keys;
        SSP_FULL_KEY sspKey;
        SSP_COMMAND_INFO info;

        // variable declarations

        // The comms window class, used to  everything sent to the validator visually and to file       CCommsWindow m_Comms;

        // The protocol version this validator is using, set in setup request
        int m_ProtocolVersion;

        // A variable to hold the type of validator, this variable is initialised using the setup request command
        char m_UnitType;

        // Two variables to hold the number of notes accepted by the validator and the value of those
        // notes when added up
        int m_NumStackedNotes;

        // Variable to hold the number of channels in the validator dataset
        int m_NumberOfChannels;

        // The multiplier by which the channel values are multiplied to give their
        // real penny value. E.g. £5.00 on channel 1, the value would be 5 and the multiplier
        // 100.
        int m_ValueMultiplier;

        //Integer to hold total number of Hold messages to be issued before releasing note from escrow
        int m_HoldNumber;

        //Integer to hold number of hold messages still to be issued
        int m_HoldCount;

        //Bool to hold flag set to true if a note is being held in escrow
        bool m_NoteHeld;

        // A list of dataset data, sorted by value. Holds the info on channel number, value, currency,
        // level and whether it is being recycled.
        List<ChannelData> m_UnitDataList;

        public bool ValidatorDisabled { get; set; } = false;

        // constructor
        public CValidator()
        {
            m_eSSP = new SSPComms();
            m_cmd = new SSP_COMMAND();
            keys = new SSP_KEYS();
            sspKey = new SSP_FULL_KEY();
            info = new SSP_COMMAND_INFO();

            m_NumberOfChannels = 0;
            m_ValueMultiplier = 1;
            m_UnitType = (char)0xFF;
            m_UnitDataList = new List<ChannelData>();
            m_HoldCount = 0;
            m_HoldNumber = 0;
        }
        public CValidator(SSPComms coms)
        {
            m_eSSP = coms;
            m_cmd = new SSP_COMMAND();
            keys = new SSP_KEYS();
            sspKey = new SSP_FULL_KEY();
            info = new SSP_COMMAND_INFO();

            m_NumberOfChannels = 0;
            m_ValueMultiplier = 1;
            m_UnitType = (char)0xFF;
            m_UnitDataList = new List<ChannelData>();
            m_HoldCount = 0;
            m_HoldNumber = 0;
        }
        public CValidator(SSPComms coms, int holdNumber)
        {
            m_eSSP = coms;
            m_cmd = new SSP_COMMAND();
            keys = new SSP_KEYS();
            sspKey = new SSP_FULL_KEY();
            info = new SSP_COMMAND_INFO();

            m_NumberOfChannels = 0;
            m_ValueMultiplier = 1;
            m_UnitType = (char)0xFF;
            m_UnitDataList = new List<ChannelData>();
            m_HoldCount = 0;
            m_HoldNumber = holdNumber;
        }
        /* Variable Access */

        // access to ssp variables
        // the pointer which gives access to library functions such as open com port, send command etc




        public SSPComms SSPComms
        {
            get { return m_eSSP; }
            set { m_eSSP = value; }
        }

        // a pointer to the command structure, this struct is filled with info and then compiled into
        // a packet by the library and sent to the validator
        public SSP_COMMAND CommandStructure
        {
            get { return m_cmd; }
            set { m_cmd = value; }
        }

        // pointer to an information structure which accompanies the command structure
        public SSP_COMMAND_INFO InfoStructure
        {
            get { return info; }
            set { info = value; }
        }



        // access to the type of unit, this will only be valid after the setup request
        public char UnitType
        {
            get { return m_UnitType; }
        }

        // access to number of channels being used by the validator
        public int NumberOfChannels
        {
            get { return m_NumberOfChannels; }
            set { m_NumberOfChannels = value; }
        }

        // access to number of notes stacked
        public int NumberOfNotesStacked
        {
            get { return m_NumStackedNotes; }
            set { m_NumStackedNotes = value; }
        }

        // access to value multiplier
        public int Multiplier
        {
            get { return m_ValueMultiplier; }
            set { m_ValueMultiplier = value; }
        }
        // acccess to hold number
        public int HoldNumber
        {
            get { return m_HoldNumber; }
            set { m_HoldNumber = value; }

        }
        //Access to flag showing note is held in escrow
        public bool NoteHeld
        {
            get { return m_NoteHeld; }
        }

        public NoteReceivedEventHandler NoteAdded { get; set; }
        public ValidatorEvent ValidatorEvent { get; set; }

        // get a channel value
        public int GetChannelValue(int channelNum)
        {
            if (channelNum >= 1 && channelNum <= m_NumberOfChannels)
            {
                foreach (ChannelData d in m_UnitDataList)
                {
                    if (d.Channel == channelNum)
                        return d.Value;
                }
            }
            return -1;
        }




        // get a channel currency
        public string GetChannelCurrency(int channelNum)
        {
            if (channelNum >= 1 && channelNum <= m_NumberOfChannels)
            {
                foreach (ChannelData d in m_UnitDataList)
                {
                    if (d.Channel == channelNum)
                        return new string(d.Currency);
                }
            }
            return "";
        }

        /* Command functions */

        // The enable command allows the validator to receive and act on commands sent to it.
        public void EnableValidator()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_ENABLE;
            m_cmd.CommandDataLength = 1;

            if (!SendCommand()) return;
            // check response
            if (CheckGenericResponses())
                Console.WriteLine("Unit enabled\r\n");
        }

        // Disable command stops the validator from acting on commands.
        public void DisableValidator()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_DISABLE;
            m_cmd.CommandDataLength = 1;

            if (!SendCommand()) return;
            // check response
            if (CheckGenericResponses())
            {
                ValidatorDisabled = true;
                Console.WriteLine("Unit disabled\r\n");
                return;
            }
            ValidatorDisabled = false;
        }
        // Return Note command returns note held in escrow to bezel. 
        public void ReturnNote()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_REJECT_BANKNOTE;
            m_cmd.CommandDataLength = 1;
            if (!SendCommand()) return;

            if (CheckGenericResponses())
            {

                Console.WriteLine("Returning note\r\n");

                m_HoldCount = 0;
            }
        }
        public void AcceptNote()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_STACK_NOTE;
            m_cmd.CommandDataLength = 1;
            if (!SendCommand()) return;

            if (CheckGenericResponses())
            {

                Console.WriteLine("Stacking Note\r\n");

                m_HoldCount = 0;
            }
        }
        // The reset command instructs the validator to restart (same effect as switching on and off)
        public void Reset()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_RESET;
            m_cmd.CommandDataLength = 1;
            if (!SendCommand()) return;

            if (CheckGenericResponses())
                Console.WriteLine("Resetting unit\r\n");
        }

        // This command just sends a sync command to the validator
        public bool SendSync()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SYNC;
            m_cmd.CommandDataLength = 1;
            if (!SendCommand()) return false;

            if (CheckGenericResponses())
                Console.WriteLine("Successfully sent sync\r\n");
            return true;
        }

        // This function sets the protocol version in the validator to the version passed across. Whoever calls
        // this needs to check the response to make sure the version is supported.
        public void SetProtocolVersion(byte pVersion)
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_HOST_PROTOCOL_VERSION;
            m_cmd.CommandData[1] = pVersion;
            m_cmd.CommandDataLength = 2;
            if (!SendCommand()) return;
        }

        // This function sends the command LAST REJECT CODE which gives info about why a note has been rejected. It then
        // outputs the info to a passed across textbox.
        public void QueryRejection()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_LAST_REJECT_CODE;
            m_cmd.CommandDataLength = 1;
            if (!SendCommand()) return;

            if (CheckGenericResponses())
            {

                switch (m_cmd.ResponseData[1])
                {
                    case 0x00: Console.WriteLine("Note accepted\r\n"); break;
                    case 0x01: Console.WriteLine("Note length incorrect\r\n"); break;
                    case 0x02: Console.WriteLine("Invalid note\r\n"); break;
                    case 0x03: Console.WriteLine("Invalid note\r\n"); break;
                    case 0x04: Console.WriteLine("Invalid note\r\n"); break;
                    case 0x05: Console.WriteLine("Invalid note\r\n"); break;
                    case 0x06: Console.WriteLine("Channel inhibited\r\n"); break;
                    case 0x07: Console.WriteLine("Second note inserted during read\r\n"); break;
                    case 0x08: Console.WriteLine("Host rejected note\r\n"); break;
                    case 0x09: Console.WriteLine("Invalid note\r\n"); break;
                    case 0x0A: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x0B: Console.WriteLine("Note too long\r\n"); break;
                    case 0x0C: Console.WriteLine("Validator disabled\r\n"); break;
                    case 0x0D: Console.WriteLine("Mechanism slow/stalled\r\n"); break;
                    case 0x0E: Console.WriteLine("Strim attempt\r\n"); break;
                    case 0x0F: Console.WriteLine("Fraud channel reject\r\n"); break;
                    case 0x10: Console.WriteLine("No notes inserted\r\n"); break;
                    case 0x11: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x12: Console.WriteLine("Twisted note detected\r\n"); break;
                    case 0x13: Console.WriteLine("Escrow time-out\r\n"); break;
                    case 0x14: Console.WriteLine("Bar code scan fail\r\n"); break;
                    case 0x15: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x16: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x17: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x18: Console.WriteLine("Invalid note read\r\n"); break;
                    case 0x19: Console.WriteLine("Incorrect note width\r\n"); break;
                    case 0x1A: Console.WriteLine("Note too short\r\n"); break;
                }
            }
        }

        // This function performs a number of commands in order to setup the encryption between the host and the validator.
        public bool NegotiateKeys()
        {
            // make sure encryption is off
            m_cmd.EncryptionStatus = false;

            // send sync
            Console.WriteLine("Syncing... ");
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SYNC;
            m_cmd.CommandDataLength = 1;

            if (!SendCommand()) return false;
            Console.WriteLine("Success");

            m_eSSP.InitiateSSPHostKeys(keys, m_cmd);

            // send generator
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SET_GENERATOR;
            m_cmd.CommandDataLength = 9;
            Console.WriteLine("Setting generator... ");

            // Convert generator to bytes and add to command data.
            BitConverter.GetBytes(keys.Generator).CopyTo(m_cmd.CommandData, 1);

            if (!SendCommand()) return false;
            Console.WriteLine("Success\r\n");

            // send modulus
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SET_MODULUS;
            m_cmd.CommandDataLength = 9;
            Console.WriteLine("Sending modulus... ");

            // Convert modulus to bytes and add to command data.
            BitConverter.GetBytes(keys.Modulus).CopyTo(m_cmd.CommandData, 1);

            if (!SendCommand()) return false;
            Console.WriteLine("Success\r\n");

            // send key exchange
            m_cmd.CommandData[0] = CCommands.SSP_CMD_REQUEST_KEY_EXCHANGE;
            m_cmd.CommandDataLength = 9;
            Console.WriteLine("Exchanging keys... ");

            // Convert host intermediate key to bytes and add to command data.
            BitConverter.GetBytes(keys.HostInter).CopyTo(m_cmd.CommandData, 1);


            if (!SendCommand()) return false;
            Console.WriteLine("Success\r\n");

            // Read slave intermediate key.
            keys.SlaveInterKey = BitConverter.ToUInt64(m_cmd.ResponseData, 1);

            m_eSSP.CreateSSPHostEncryptionKey(keys);

            // get full encryption key
            m_cmd.Key.FixedKey = 0x0123456701234567;
            m_cmd.Key.VariableKey = keys.KeyHost;

            Console.WriteLine("Keys successfully negotiated\r\n");

            return true;
        }

        // This function uses the setup request command to get all the information about the validator.
        public void ValidatorSetupRequest()
        {
            StringBuilder sbDisplay = new StringBuilder(1000);

            // send setup request
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SETUP_REQUEST;
            m_cmd.CommandDataLength = 1;

            if (!SendCommand()) return;

            // display setup request


            // unit type
            int index = 1;
            sbDisplay.Append("Unit Type: ");
            m_UnitType = (char)m_cmd.ResponseData[index++];
            switch (m_UnitType)
            {
                case (char)0x00: sbDisplay.Append("Validator"); break;
                case (char)0x03: sbDisplay.Append("SMART Hopper"); break;
                case (char)0x06: sbDisplay.Append("SMART Payout"); break;
                case (char)0x07: sbDisplay.Append("NV11"); break;
                case (char)0x0D: sbDisplay.Append("TEBS"); break;
                default: sbDisplay.Append("Unknown Type"); break;
            }

            // firmware
            sbDisplay.AppendLine();
            sbDisplay.Append("Firmware: ");

            sbDisplay.Append((char)m_cmd.ResponseData[index++]);
            sbDisplay.Append((char)m_cmd.ResponseData[index++]);
            sbDisplay.Append(".");
            sbDisplay.Append((char)m_cmd.ResponseData[index++]);
            sbDisplay.Append((char)m_cmd.ResponseData[index++]);

            sbDisplay.AppendLine();
            // country code.
            // legacy code so skip it.
            index += 3;

            // value multiplier.
            // legacy code so skip it.
            index += 3;

            // Number of channels
            sbDisplay.AppendLine();
            sbDisplay.Append("Number of Channels: ");
            m_NumberOfChannels = m_cmd.ResponseData[index++];
            sbDisplay.Append(m_NumberOfChannels);
            sbDisplay.AppendLine();

            // channel values.
            // legacy code so skip it.
            index += m_NumberOfChannels; // Skip channel values

            // channel security
            // legacy code so skip it.
            index += m_NumberOfChannels;

            // real value multiplier
            // (big endian)
            sbDisplay.Append("Real Value Multiplier: ");
            m_ValueMultiplier = m_cmd.ResponseData[index + 2];
            m_ValueMultiplier += m_cmd.ResponseData[index + 1] << 8;
            m_ValueMultiplier += m_cmd.ResponseData[index] << 16;
            sbDisplay.Append(m_ValueMultiplier);
            sbDisplay.AppendLine();
            index += 3;


            // protocol version
            sbDisplay.Append("Protocol Version: ");
            m_ProtocolVersion = m_cmd.ResponseData[index++];
            sbDisplay.Append(m_ProtocolVersion);
            sbDisplay.AppendLine();

            // Add channel data to list then display.
            // Clear list.
            m_UnitDataList.Clear();
            // Loop through all channels.

            for (byte i = 0; i < m_NumberOfChannels; i++)
            {
                ChannelData loopChannelData = new ChannelData();
                // Channel number.
                loopChannelData.Channel = (byte)(i + 1);

                // Channel value.
                loopChannelData.Value = BitConverter.ToInt32(m_cmd.ResponseData, index + (m_NumberOfChannels * 3) + (i * 4)) * m_ValueMultiplier;

                // Channel Currency
                loopChannelData.Currency[0] = (char)m_cmd.ResponseData[index + (i * 3)];
                loopChannelData.Currency[1] = (char)m_cmd.ResponseData[(index + 1) + (i * 3)];
                loopChannelData.Currency[2] = (char)m_cmd.ResponseData[(index + 2) + (i * 3)];

                // Channel level.
                loopChannelData.Level = 0;

                // Channel recycling
                loopChannelData.Recycling = false;

                // Add data to list.
                m_UnitDataList.Add(loopChannelData);

                //Display data
                sbDisplay.Append("Channel ");
                sbDisplay.Append(loopChannelData.Channel);
                sbDisplay.Append(": ");
                sbDisplay.Append(loopChannelData.Value / m_ValueMultiplier);
                sbDisplay.Append(" ");
                sbDisplay.Append(loopChannelData.Currency);
                sbDisplay.AppendLine();
            }

            // Sort the list by .Value.
            m_UnitDataList.Sort((d1, d2) => d1.Value.CompareTo(d2.Value));


            Console.WriteLine(sbDisplay.ToString());
        }

        // This function sends the set inhibits command to set the inhibits on the validator. An additional two
        // bytes are sent along with the command byte to indicate the status of the inhibits on the channels.
        // For example 0xFF and 0xFF in binary is 11111111 11111111. This indicates all 16 channels supported by
        // the validator are uninhibited. If a user wants to inhibit channels 8-16, they would send 0x00 and 0xFF.
        public void SetInhibits()
        {
            var t = Convert.ToInt32("11111111", 2);
            // set inhibits
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SET_CHANNEL_INHIBITS;
            m_cmd.CommandData[1] = 0xFF;
            m_cmd.CommandData[2] = 0xFF;
            m_cmd.CommandDataLength = 3;

            if (!SendCommand()) return;
            if (CheckGenericResponses())
            {
                Console.WriteLine("Inhibits set\r\n");
            }
        }
        public void SetInhibits(byte ch)
        {
            // set inhibits
            m_cmd.CommandData[0] = CCommands.SSP_CMD_SET_CHANNEL_INHIBITS;
            m_cmd.CommandData[1] = ch;
            m_cmd.CommandData[2] = 0xFF;
            m_cmd.CommandDataLength = 3;

            if (!SendCommand()) return;
            if (CheckGenericResponses())
            {
                Console.WriteLine("Inhibits set\r\n");
            }
        }
        // This function gets the serial number of the device.  An optional Device parameter can be used
        // for TEBS systems to specify which device's serial number should be returned.
        // 0x00 = NV200
        // 0x01 = SMART Payout
        // 0x02 = Tamper Evident Cash Box.
        public void GetSerialNumber(byte Device)
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_GET_SERIAL_NUMBER;
            m_cmd.CommandData[1] = Device;
            m_cmd.CommandDataLength = 2;


            if (!SendCommand()) return;
            if (CheckGenericResponses())
            {
                // Response data is big endian, so reverse bytes 1 to 4.
                Array.Reverse(m_cmd.ResponseData, 1, 4);
                Console.WriteLine("Serial Number Device " + Device + ": ");
                Console.WriteLine(BitConverter.ToUInt32(m_cmd.ResponseData, 1).ToString());
                Console.WriteLine("\r\n");
            }
        }

        public void GetSerialNumber()
        {
            m_cmd.CommandData[0] = CCommands.SSP_CMD_GET_SERIAL_NUMBER;
            m_cmd.CommandDataLength = 1;

            if (!SendCommand()) return;
            if (CheckGenericResponses())
            {
                // Response data is big endian, so reverse bytes 1 to 4.
                Array.Reverse(m_cmd.ResponseData, 1, 4);
                Console.WriteLine("Serial Number ");
                Console.WriteLine(": ");
                Console.WriteLine(BitConverter.ToUInt32(m_cmd.ResponseData, 1).ToString());
                Console.WriteLine("\r\n");
            }
        }
        // The poll function is called repeatedly to poll to validator for information, it returns as
        // a response in the command structure what events are currently happening.
        public bool DoPoll()
        {
            byte i;
            // If a not is to be held in escrow, send hold commands, as poll releases note.
            if (m_HoldCount > 0)
            {
                m_NoteHeld = true;
                m_HoldCount--;
                m_cmd.CommandData[0] = CCommands.SSP_CMD_HOLD;
                m_cmd.CommandDataLength = 1;
                Console.WriteLine("Note held in escrow: " + m_HoldCount + "\r\n");
                if (!SendCommand())
                    return false;
                return true;

            }
            //send poll
            m_cmd.CommandData[0] = CCommands.SSP_CMD_POLL;
            m_cmd.CommandDataLength = 1;
            m_NoteHeld = false;

            if (!SendCommand())
                return false;

            //parse poll response
            int noteVal = 0;
            for (i = 1; i < m_cmd.ResponseDataLength; i++)
            {
                switch (m_cmd.ResponseData[i])
                {
                    // This response indicates that the unit was reset and this is the first time a poll
                    // has been called since the reset.
                    case CCommands.SSP_POLL_SLAVE_RESET:
                        Console.WriteLine("Unit reset\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_SLAVE_RESET, null);
                        break;
                    // A note is currently being read by the validator sensors. The second byte of this response
                    // is zero until the note's type has been determined, it then changes to the channel of the 
                    // scanned note.
                    case CCommands.SSP_POLL_READ_NOTE:
                        if (m_cmd.ResponseData[i + 1] > 0)
                        {
                            noteVal = GetChannelValue(m_cmd.ResponseData[i + 1]);
                            Console.WriteLine("Note in escrow, amount: " + CHelpers.FormatToCurrency(noteVal) + " " + GetChannelCurrency(m_cmd.ResponseData[i + 1]) + "\r\n");
                            m_HoldCount = m_HoldNumber;
                            ValidatorEvent?.Invoke(PollResponse.SSP_POLL_READ_NOTE, new ValidatorCredit() { Amount = Convert.ToDecimal(CHelpers.FormatToCurrency(noteVal)), Name = noteVal.ToString() });
                        }
                        else
                            Console.WriteLine("Reading note...\r\n");
                        i++;
                        break;
                    // A credit event has been detected, this is when the validator has accepted a note as legal currency.
                    case CCommands.SSP_POLL_CREDIT_NOTE:
                        noteVal = GetChannelValue(m_cmd.ResponseData[i + 1]);
                        var currencyString = CHelpers.FormatToCurrency(noteVal);
                        var chCurrency = GetChannelCurrency(m_cmd.ResponseData[i + 1]);
                        Console.WriteLine("Credit " + CHelpers.FormatToCurrency(noteVal) + " " + GetChannelCurrency(m_cmd.ResponseData[i + 1]) + "\r\n");
                        NoteAdded?.Invoke(new ValidatorCredit() { Amount = Convert.ToDecimal(currencyString), Name = chCurrency });
                        m_NumStackedNotes++;
                        i++;
                        break;
                    // A note is being rejected from the validator. This will carry on polling while the note is in transit.
                    case CCommands.SSP_POLL_NOTE_REJECTING:
                        Console.WriteLine("Rejecting note...\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_REJECTING, null);
                        break;
                    // A note has been rejected from the validator, the note will be resting in the bezel. This response only
                    // appears once.
                    case CCommands.SSP_POLL_NOTE_REJECTED:
                        Console.WriteLine("Note rejected\r\n");
                        QueryRejection();
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_REJECTED, null);
                        break;
                    // A note is in transit to the cashbox.
                    case CCommands.SSP_POLL_NOTE_STACKING:
                        Console.WriteLine("Stacking note...\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_STACKING, null);
                        break;
                    // A note has reached the cashbox.
                    case CCommands.SSP_POLL_NOTE_STACKED:
                        Console.WriteLine("Note stacked\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_STACKED, null);
                        break;
                    // A safe jam has been detected. This is where the user has inserted a note and the note
                    // is jammed somewhere that the user cannot reach.
                    case CCommands.SSP_POLL_SAFE_NOTE_JAM:
                        Console.WriteLine("Safe jam\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_SAFE_NOTE_JAM, null);
                        break;
                    // An unsafe jam has been detected. This is where a user has inserted a note and the note
                    // is jammed somewhere that the user can potentially recover the note from.
                    case CCommands.SSP_POLL_UNSAFE_NOTE_JAM:
                        Console.WriteLine("Unsafe jam\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_UNSAFE_NOTE_JAM, null);
                        break;
                    // The validator is disabled, it will not execute any commands or do any actions until enabled.
                    case CCommands.SSP_POLL_DISABLED:
                        ValidatorDisabled = true;
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_DISABLED, null);
                        break;
                    // A fraud attempt has been detected. The second byte indicates the channel of the note that a fraud
                    // has been attempted on.
                    case CCommands.SSP_POLL_FRAUD_ATTEMPT:
                        Console.WriteLine("Fraud attempt, note type: " + GetChannelValue(m_cmd.ResponseData[i + 1]) + "\r\n");
                        i++;
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_FRAUD_ATTEMPT, new ValidatorCredit { Amount = GetChannelValue(m_cmd.ResponseData[i + 1]), Name = "" });
                        break;
                    // The stacker (cashbox) is full. 
                    case CCommands.SSP_POLL_STACKER_FULL:
                        Console.WriteLine("Stacker full\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_STACKER_FULL, null);
                        break;
                    // A note was detected somewhere inside the validator on startup and was rejected from the front of the
                    // unit.
                    case CCommands.SSP_POLL_NOTE_CLEARED_FROM_FRONT:
                        Console.WriteLine(GetChannelValue(m_cmd.ResponseData[i + 1]) + " note cleared from front at reset." + "\r\n");
                        i++;
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_CLEARED_FROM_FRONT, null);
                        break;
                    // A note was detected somewhere inside the validator on startup and was cleared into the cashbox.
                    case CCommands.SSP_POLL_NOTE_CLEARED_TO_CASHBOX:
                        Console.WriteLine(GetChannelValue(m_cmd.ResponseData[i + 1]) + " note cleared to stacker at reset." + "\r\n");
                        i++;
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_CLEARED_TO_CASHBOX, null);
                        break;
                    // The cashbox has been removed from the unit. This will continue to poll until the cashbox is replaced.
                    case CCommands.SSP_POLL_CASHBOX_REMOVED:
                        Console.WriteLine("Cashbox removed...\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_CASHBOX_REMOVED, null);
                        break;
                    // The cashbox has been replaced, this will only display on a poll once.
                    case CCommands.SSP_POLL_CASHBOX_REPLACED:
                        Console.WriteLine("Cashbox replaced\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_CASHBOX_REPLACED, null);
                        break;
                    // A bar code ticket has been detected and validated. The ticket is in escrow, continuing to poll will accept
                    // the ticket, sending a reject command will reject the ticket.
                    //case CCommands.SSP_POLL_BAR_CODE_VALIDATED:
                    //    Console.WriteLine("Bar code ticket validated\r\n");
                    //    break;
                    //// A bar code ticket has been accepted (equivalent to note credit).
                    //case CCommands.SSP_POLL_BAR_CODE_ACK:
                    //    Console.WriteLine("Bar code ticket accepted\r\n");
                    //    break;
                    // The validator has detected its note path is open. The unit is disabled while the note path is open.
                    // Only available in protocol versions 6 and above.
                    case CCommands.SSP_POLL_NOTE_PATH_OPEN:
                        Console.WriteLine("Note path open\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_NOTE_PATH_OPEN, null);
                        break;
                    // All channels on the validator have been inhibited so the validator is disabled. Only available on protocol
                    // versions 7 and above.
                    case CCommands.SSP_POLL_CHANNEL_DISABLE:
                        Console.WriteLine("All channels inhibited, unit disabled\r\n");
                        ValidatorEvent?.Invoke(PollResponse.SSP_POLL_CHANNEL_DISABLE, null);
                        break;
                    default:
                        Console.WriteLine("Unrecognised poll response detected " + (int)m_cmd.ResponseData[i] + "\r\n");
                        ValidatorEvent?.Invoke(PollResponse.UNRECOGNISED_POLL, null);
                        break;
                }
            }
            return true;
        }

        /* Non-Command functions */

        // This function calls the open com port function of the SSP library.
        public bool OpenComPort()
        {

            Console.WriteLine("Opening com port\r\n");
            if (!m_eSSP.OpenSSPComPort(m_cmd))
            {
                return false;
            }
            return true;
        }



        public bool OpenComPort(SerialPort com)
        {

            Console.WriteLine("Opening com port\r\n");
            if (!m_eSSP.OpenSSPComPort(m_cmd, com))
            {
                return false;
            }
            return true;
        }
        /* Exception and Error Handling */

        // This is used for generic response error catching, it outputs the info in a
        // meaningful way.
        private bool CheckGenericResponses()
        {
            if (m_cmd.ResponseData[0] == CCommands.SSP_RESPONSE_OK)
                return true;
            else
            {
                if (true)
                {
                    switch (m_cmd.ResponseData[0])
                    {
                        case CCommands.SSP_RESPONSE_COMMAND_CANNOT_BE_PROCESSED:
                            if (m_cmd.ResponseData[1] == 0x03)
                            {
                                Console.WriteLine("Validator has responded with \"Busy\", command cannot be processed at this time\r\n");
                            }
                            else
                            {
                                Console.WriteLine("Command response is CANNOT PROCESS COMMAND, error code - 0x"
                                + BitConverter.ToString(m_cmd.ResponseData, 1, 1) + "\r\n");
                            }
                            return false;
                        case CCommands.SSP_RESPONSE_FAIL:
                            Console.WriteLine("Command response is FAIL\r\n");
                            return false;
                        case CCommands.SSP_RESPONSE_KEY_NOT_SET:
                            Console.WriteLine("Command response is KEY NOT SET, Validator requires encryption on this command or there is"
                                + "a problem with the encryption on this request\r\n");
                            return false;
                        case CCommands.SSP_RESPONSE_PARAMETER_OUT_OF_RANGE:
                            Console.WriteLine("Command response is PARAM OUT OF RANGE\r\n");
                            return false;
                        case CCommands.SSP_RESPONSE_SOFTWARE_ERROR:
                            Console.WriteLine("Command response is SOFTWARE ERROR\r\n");
                            return false;
                        case CCommands.SSP_RESPONSE_COMMAND_NOT_KNOWN:
                            Console.WriteLine("Command response is UNKNOWN\r\n");
                            return false;
                        case CCommands.SSP_RESPONSE_WRONG_NO_PARAMETERS:
                            Console.WriteLine("Command response is WRONG PARAMETERS\r\n");
                            return false;
                        default:
                            return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }
        public bool RejectNote()
        {
            // Backup data and length in case we need to retry
            byte[] backup = new byte[255];
            m_cmd.CommandData.CopyTo(backup, 0);
            byte length = m_cmd.CommandDataLength;

            // attempt to send the command
            if (m_eSSP.SSPSendCommand(m_cmd, info) == false)
            {
                m_eSSP.CloseComPort();

                Console.WriteLine("Sending command failed\r\nPort status: " + m_cmd.ResponseStatus.ToString() + "\r\n");
                return false;
            }



            return true;
        }

        public bool SendCommand()
        {
            // Backup data and length in case we need to retry
            byte[] backup = new byte[255];
            m_cmd.CommandData.CopyTo(backup, 0);
            byte length = m_cmd.CommandDataLength;

            // attempt to send the command
            if (m_eSSP.SSPSendCommand(m_cmd, info) == false)
            {
                m_eSSP.CloseComPort();

                Console.WriteLine("Sending command failed\r\nPort status: " + m_cmd.ResponseStatus.ToString() + "\r\n");
                return false;
            }



            return true;
        }
    };
    public class ChannelData
    {
        public int Value;
        public byte Channel;
        public char[] Currency;
        public int Level;
        public bool Recycling;
        public ChannelData()
        {
            Value = 0;
            Channel = 0;
            Currency = new char[3];
            Level = 0;
            Recycling = false;
        }
    };

    public class CHelpers
    {
        // Helper function to convert 4 bytes to an int 32 from a specified array and index.
        static public int ConvertBytesToInt32(byte[] b, int index)
        {
            return BitConverter.ToInt32(b, index);
        }

        // 2 bytes to int 16
        static public int ConvertBytesToInt16(byte[] b, int index)
        {
            return BitConverter.ToInt16(b, index);
        }

        // Convert int32 to byte array
        static public byte[] ConvertIntToBytes(int n)
        {
            return BitConverter.GetBytes(n);
        }

        // Convert int16 to byte array
        static public byte[] ConvertIntToBytes(short n)
        {
            return BitConverter.GetBytes(n);
        }

        // Convert int8 to byte
        static public byte[] ConvertIntToBytes(char n)
        {
            return BitConverter.GetBytes(n);
        }

        // Helper uses value of channel and adds a decimal point and two zeroes, also adds current
        // currency.
        static public string FormatToCurrency(int unformattedNumber)
        {
            float f = unformattedNumber * 0.01f;
            return f.ToString("0.00");
        }

        // This helper takes a byte and returns the command/response name as a string.
        static public string ConvertByteToName(byte b)
        {
            switch (b)
            {
                case 0x01:
                    return "RESET COMMAND";
                case 0x11:
                    return "SYNC COMMAND";
                case 0x4A:
                    return "SET GENERATOR COMMAND";
                case 0x4B:
                    return "SET MODULUS COMMAND";
                case 0x4C:
                    return "KEY EXCHANGE COMMAND";
                case 0x2:
                    return "SET INHIBITS COMMAND";
                case 0xA:
                    return "ENABLE COMMAND";
                case 0x09:
                    return "DISABLE COMMAND";
                case 0x7:
                    return "POLL COMMAND";
                case 0x05:
                    return "SETUP REQUEST COMMAND";
                case 0x03:
                    return "DISPLAY ON COMMAND";
                case 0x04:
                    return "DISPLAY OFF COMMAND";
                case 0x5C:
                    return "ENABLE PAYOUT COMMAND";
                case 0x5B:
                    return "DISABLE PAYOUT COMMAND";
                case 0x3B:
                    return "SET ROUTING COMMAND";
                case 0x45:
                    return "SET VALUE REPORTING TYPE COMMAND";
                case 0X42:
                    return "PAYOUT LAST NOTE COMMAND";
                case 0x3F:
                    return "EMPTY COMMAND";
                case 0x41:
                    return "GET NOTE POSITIONS COMMAND";
                case 0x43:
                    return "STACK LAST NOTE COMMAND";
                case 0xF1:
                    return "RESET RESPONSE";
                case 0xEF:
                    return "NOTE READ RESPONSE";
                case 0xEE:
                    return "CREDIT RESPONSE";
                case 0xED:
                    return "REJECTING RESPONSE";
                case 0xEC:
                    return "REJECTED RESPONSE";
                case 0xCC:
                    return "STACKING RESPONSE";
                case 0xEB:
                    return "STACKED RESPONSE";
                case 0xEA:
                    return "SAFE JAM RESPONSE";
                case 0xE9:
                    return "UNSAFE JAM RESPONSE";
                case 0xE8:
                    return "DISABLED RESPONSE";
                case 0xE6:
                    return "FRAUD ATTEMPT RESPONSE";
                case 0xE7:
                    return "STACKER FULL RESPONSE";
                case 0xE1:
                    return "NOTE CLEARED FROM FRONT RESPONSE";
                case 0xE2:
                    return "NOTE CLEARED TO CASHBOX RESPONSE";
                case 0xE3:
                    return "CASHBOX REMOVED RESPONSE";
                case 0xE4:
                    return "CASHBOX REPLACED RESPONSE";
                case 0xDB:
                    return "NOTE STORED RESPONSE";
                case 0xDA:
                    return "NOTE DISPENSING RESPONSE";
                case 0xD2:
                    return "NOTE DISPENSED RESPONSE";
                case 0xC9:
                    return "NOTE TRANSFERRED TO STACKER RESPONSE";
                case 0xF0:
                    return "OK RESPONSE";
                case 0xF2:
                    return "UNKNOWN RESPONSE";
                case 0xF3:
                    return "WRONG PARAMS RESPONSE";
                case 0xF4:
                    return "PARAM OUT OF RANGE RESPONSE";
                case 0xF5:
                    return "CANNOT PROCESS RESPONSE";
                case 0xF6:
                    return "SOFTWARE ERROR RESPONSE";
                case 0xF8:
                    return "FAIL RESPONSE";
                case 0xFA:
                    return "KEY NOT SET RESPONSE";
                default:
                    return "Byte command name unsupported";
            }
        }
    }
}
