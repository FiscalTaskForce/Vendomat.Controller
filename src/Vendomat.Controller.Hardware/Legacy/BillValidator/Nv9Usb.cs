using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vendomat.Common.SSP;

namespace Vendomat.Common.BillValidator
{
    public class NV9USB
    {
        SerialPort serialCom;
        SSPComms ssp = new SSPComms();
        SSP_COMMAND comms = new SSP_COMMAND();
        SSP_COMMAND_INFO iNFO = new SSP_COMMAND_INFO();
        public CValidator Validator;
        public bool Running = false;
        volatile bool Connected = false, ConnectionFail = false; // Threading bools to indicate status of connection with validator
        Task ConnectionThread;


        public NV9USB(string _deviceName, int baudRate)
        {
            serialCom = new SerialPort(_deviceName, baudRate);
            Validator = new CValidator(ssp);
        }
        public NV9USB(string _deviceName, int baudRate, bool escrow)
        {
            serialCom = new SerialPort(_deviceName, baudRate);
            //serialCom = new NativeSerial(_deviceName, baudRate);
            Validator = new CValidator(ssp, escrow ? 1 : 0);

        }
        public async Task MainLoop()
        {

            if (Running)
            {
                Running = false;
                return;
            }

            await Task.Run(async () => {
                Validator.CommandStructure.Timeout = 3000;

                // connect to the validator
                if (ConnectToValidator())
                {
                    Running = true;

                }


                while (Running)
                {
                    try
                    {
                        // if the poll fails, try to reconnect
                        if (!Validator.DoPoll())
                        {
                            Running = false;
                            Connected = false;
                            await Task.Run(ConnectToValidatorThreaded);

                            while (!Connected)
                            {
                                await Task.Run(ConnectToValidatorThreaded);
                                if (ConnectionFail)
                                {

                                    break;
                                }

                            }

                        }
                        else
                        {
                            ;
                        }
                        if (Validator.ValidatorDisabled)
                            Validator.EnableValidator();
                    }
                    catch (Exception ex)
                    {

                        throw;
                    }





                }

                //close com port and threads
                Validator.SSPComms.CloseComPort();

            });

        }
        int reconnectionAttempts = 10, reconnectionInterval = 3;
        System.Timers.Timer reconnectionTimer = new System.Timers.Timer();
        private bool ConnectToValidator()
        {
            // setup the timer
            reconnectionTimer.Interval = reconnectionInterval * 1000; // for ms

            // run for number of attempts specified
            for (int i = 0; i < reconnectionAttempts; i++)
            {
                // reset timer
                reconnectionTimer.Enabled = true;

                // close com port in case it was open
                Validator.SSPComms.CloseComPort();

                // turn encryption off for first stage
                Validator.CommandStructure.EncryptionStatus = false;

                // open com port and negotiate keys
                if (Validator.OpenComPort(serialCom) && Validator.NegotiateKeys())
                {
                    Validator.CommandStructure.EncryptionStatus = true; // now encrypting
                    // find the max protocol version this validator supports
                    byte maxPVersion = FindMaxProtocolVersion();
                    if (maxPVersion > 6)
                    {
                        Validator.SetProtocolVersion(maxPVersion);
                    }
                    else
                    {
                        Console.WriteLine("This program does not support units under protocol version 6, update firmware.", "ERROR");
                        return false;
                    }
                    // get info from the validator and store useful vars
                    Validator.ValidatorSetupRequest();
                    // Get Serial number
                    Validator.GetSerialNumber();
                    // check this unit is supported by this program
                    if (!IsUnitTypeSupported(Validator.UnitType))
                    {
                        Console.WriteLine("Unsupported unit type, this SDK supports the BV series and the NV series (excluding the NV11)");

                        return false;
                    }
                    // inhibits, this sets which channels can receive notes
                    Validator.SetInhibits();
                    // enable, this allows the validator to receive and act on commands
                    Validator.EnableValidator();

                    return true;
                }

            }
            return false;
        }
        private void ConnectToValidatorThreaded()
        {
            // setup the timer
            reconnectionTimer.Interval = reconnectionInterval * 1000; // for ms

            // run for number of attempts specified
            for (int i = 0; i < reconnectionAttempts; i++)
            {
                // reset timer
                reconnectionTimer.Enabled = true;

                // close com port in case it was open
                Validator.SSPComms.CloseComPort();

                // turn encryption off for first stage
                Validator.CommandStructure.EncryptionStatus = false;

                // open com port and negotiate keys
                if (Validator.OpenComPort() && Validator.NegotiateKeys())
                {
                    Validator.CommandStructure.EncryptionStatus = true; // now encrypting
                    // find the max protocol version this validator supports
                    byte maxPVersion = FindMaxProtocolVersion();
                    if (maxPVersion > 6)
                    {
                        Validator.SetProtocolVersion(maxPVersion);
                    }
                    else
                    {

                        Connected = false;
                        return;
                    }
                    // get info from the validator and store useful vars
                    Validator.ValidatorSetupRequest();
                    // inhibits, this sets which channels can receive notes
                    Validator.SetInhibits();
                    // enable, this allows the validator to operate
                    Validator.EnableValidator();

                    Connected = true;
                    return;
                }

            }
            Connected = false;
            ConnectionFail = true;
        }
        private bool IsUnitTypeSupported(char type)
        {
            if (type == (char)0x00)
                return true;
            return false;
        }
        public void EnableValidator()
        {
            comms.CommandData[0] = CCommands.SSP_CMD_ENABLE;
            comms.CommandDataLength = 1;

            if (!SendCommand()) return;
            // check response
            if (CheckGenericResponses())
                Console.WriteLine("Unit enabled\r\n");
        }
        public bool SendCommand()
        {
            // Backup data and length in case we need to retry
            byte[] backup = new byte[255];
            comms.CommandData.CopyTo(backup, 0);
            byte length = comms.CommandDataLength;

            // attempt to send the command
            if (ssp.SSPSendCommand(comms, iNFO) == false)
            {
                ssp.CloseComPort();

                Console.WriteLine("Sending command failed\r\nPort status: " + comms.ResponseStatus.ToString() + "\r\n");
                return false;
            }



            return true;
        }
        private byte FindMaxProtocolVersion()
        {
            // not dealing with protocol under level 6
            // attempt to set in validator
            byte b = 0x06;
            while (true)
            {
                Validator.SetProtocolVersion(b);
                if (Validator.CommandStructure.ResponseData[0] == CCommands.SSP_RESPONSE_FAIL)
                    return --b;
                b++;
                if (b > 20)
                    return 0x06; // return default if protocol 'runs away'
            }
        }
        private bool CheckGenericResponses()
        {
            if (comms.ResponseData[0] == CCommands.SSP_RESPONSE_OK)
                return true;
            else
            {
                if (true)
                {
                    switch (comms.ResponseData[0])
                    {
                        case CCommands.SSP_RESPONSE_COMMAND_CANNOT_BE_PROCESSED:
                            if (comms.ResponseData[1] == 0x03)
                            {
                                Console.WriteLine("Validator has responded with \"Busy\", command cannot be processed at this time\r\n");
                            }
                            else
                            {
                                Console.WriteLine("Command response is CANNOT PROCESS COMMAND, error code - 0x"
                                + BitConverter.ToString(comms.ResponseData, 1, 1) + "\r\n");
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

        public void ReturnNote()
        {
            Validator.ReturnNote();
        }
        public void AcceptNote()
        {
            Validator.AcceptNote();
        }
    }
}
