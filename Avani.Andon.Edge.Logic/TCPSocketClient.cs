﻿using EasyNetQ;
using SuperSocket.ClientEngine;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avani.Helper;
using Newtonsoft.Json;
using iAndon.MSG;
using System.Timers;

public delegate void ReceiveIncomingDataEventRaise(string message);

namespace Avani.Andon.Edge.Logic
{
    public class TCPSocketClient : AsyncTcpSession
    {

        private Log _Logger = Avani.Andon.Edge.Logic.Helper.GetLog();
        private readonly string _LogCategory = "TCPSocketClient";
        private string _LogPath = ConfigurationManager.AppSettings["log_path"];

        public event ReceiveIncomingDataEventRaise OnReceivedMessage;

        private string _RabbitMQHost = ConfigurationManager.AppSettings["RabbitMQ.Host"];
        private string _RabbitMQVirtualHost = ConfigurationManager.AppSettings["RabbitMQ.VirtualHost"];
        private string _RabbitMQUser = ConfigurationManager.AppSettings["RabbitMQ.User"];
        private string _RabbitMQPassword = ConfigurationManager.AppSettings["RabbitMQ.Password"];
        private int _ReconnectInterval = 60 * 1000 * int.Parse(ConfigurationManager.AppSettings["reconnect_interval"]); //Tính = phút

        private int _Disconnect_Interval = 1000 * int.Parse(ConfigurationManager.AppSettings["disconnect_interval"]);
        private int _Error_Interval = int.Parse(ConfigurationManager.AppSettings["error_interval"]);
        private int _RequestInterval = 1000 * int.Parse(ConfigurationManager.AppSettings["request_interval"]); //Tính = second
        private int _SendInterval = 1000 * int.Parse(ConfigurationManager.AppSettings["send_interval"]); //Tính = second


        private int _PingInterval = 1000*int.Parse(ConfigurationManager.AppSettings["ping_interval"]); //Tính = second
        private bool _IsPingInterval = (ConfigurationManager.AppSettings["ping_client"] == "1");
        private string _PingMessage = ConfigurationManager.AppSettings["ping_message"];
        private int _MessageLength = int.Parse(ConfigurationManager.AppSettings["message_length"]);
        private int _DeviceNumberOnGateway = int.Parse(ConfigurationManager.AppSettings["DeviceNumberOnGateway"]);

        private Dictionary<int, Andon_MSG> Nodes = new Dictionary<int, Andon_MSG>();

        //public static int TIMER_SCHEDULE_CHECK_DISCONNECT = Convert.ToInt32(ConfigurationSettings.AppSettings["TIMER_SCHEDULE_CHECK_DISCONNECT"].Trim());//mili giay
        //public static int TIME_CONFIRM_DISCONNECT = Convert.ToInt32(ConfigurationSettings.AppSettings["TIME_CONFIRM_DISCONNECT"].Trim());//phut
        public static int TIME_WAIT_CONNECT = Convert.ToInt32(ConfigurationSettings.AppSettings["TIME_WAIT_CONNECT"].Trim());//mili giay

        public static int TIME_SLEEP_SEND = Convert.ToInt32(ConfigurationSettings.AppSettings["TIME_SLEEP_SEND"].Trim());//mili giay

        public static int TIME_NOT_RECEIVE_DATA = Convert.ToInt32(ConfigurationSettings.AppSettings["TIME_NOT_RECEIVE_DATA"].Trim());//giay

        public static int SEND_RESET_NOT_RESPONSE = Convert.ToInt32(ConfigurationSettings.AppSettings["SEND_RESET_NOT_RESPONSE"].Trim());//giay

        private bool _IsInvertInput = (ConfigurationManager.AppSettings["INVERT_INPUT"] == "1");

        public static byte NumberOfRegister = 6;


        private System.Timers.Timer _TimerPingCommand = new System.Timers.Timer();
        private System.Timers.Timer _TimerSendCommand = new System.Timers.Timer();
        private System.Timers.Timer _TimerReconnect = new System.Timers.Timer();

        public int Id { get; set; }
        public string ServerIP { get; set; }
        public int NumberOfNodes { get; set; }
        public int ServerPort { get; set; }
        public int CountForNoData { get; set; } //Dùng để đếm xem sau bao nhiêu lần ko nhận được dữ liệu thì reset ngay

        private IBus _EventBus;

        IPEndPoint remoteEndpoint;

        private string strAvCache = "";
        public DateTime LastTimeReceiveData = DateTime.Now;

        bool isRunning = false;
        readonly object lockReceiveData = new object();
        readonly object lockConnectSync = new object();

        public TCPSocketClient(int _id, string _ip, int _port, int _numberOfNodes = 0)
        {
            try
            {
                Id = _id;
                ServerIP = _ip;
                ServerPort = _port;
                NumberOfNodes = _numberOfNodes;

                //Setup Nodes
                for (int i = 1; i <= _numberOfNodes; i++)
                {
                    Nodes.Add(i, new Andon_MSG(ServerIP, LastTimeReceiveData, MessageType.Andon, i.ToString(), 0, 0, 0, 0, 0, 0));
                }
                //rabbit = _rabbit;
                Setup();
                ConnectRabbitMQ();
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Error when int Gateway {_ip} - Port {_port} - Nodes {_numberOfNodes}: {ex.Message}", LogType.Error);
            }

        }

        void Setup()
        {
            isRunning = true;
            remoteEndpoint = new IPEndPoint(IPAddress.Parse(ServerIP), ServerPort);

            Connected += SessionConnected;
            Closed += SessionClosed;
            DataReceived += SessionOnDataReceived;
            Error += SessionError;
            CountForNoData = 0;
            //_Logger = Avani.Andon.Edge.Logic.Helper.GetLog();

            //Send PING Command
            _TimerPingCommand.Interval = _RequestInterval;
            _TimerPingCommand.Elapsed += _TimerProccessPingCommand_Elapsed;
            _TimerPingCommand.Start();


            //Send Control Command
            _TimerSendCommand.Interval = _SendInterval;
            _TimerSendCommand.Elapsed += _TimerProccessSendCommand_Elapsed;
            _TimerSendCommand.Start();


            //Reconnect gateway 
            _TimerReconnect.Interval = _ReconnectInterval;
            _TimerReconnect.Elapsed += _TimerReconnect_Elapsed;
            _TimerReconnect.Start();


        }

        private void _TimerProccessPingCommand_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _TimerPingCommand.Stop();
                if (_IsPingInterval)
                {
                    //byte[] bytes = Encoding.ASCII.GetBytes(_PingMessage + "\r\n");
                    //this.Client.Send(bytes);
                    //Thread.Sleep(TIME_SLEEP_SEND);

                    //ushort _startAddress = 0, _numberOfRegisters = 2;
                    //byte _functionCode = 3;
                    string _msg = "";
                    for (byte _node = 1; _node <= NumberOfNodes; _node++)
                    {
                        //Gửi lệnh request 
                        //int _nodeId = (this.Id - 1) * _DeviceNumberOnGateway + _node;

                        _msg = new AndonMessage().PackageRequest(_node);
                        byte[] data = Encoding.ASCII.GetBytes(_msg);

                        //_msg = _msg.Replace("\r\n", "");

                        //byte[] data = ModbusRTUOverTCP.ReadHoldingRegistersMsg(_node, _startAddress, _functionCode, _numberOfRegisters);

                        _Logger.Write(_LogCategory, $"Ping Client {this.ServerIP} - Slave {_node}: {_msg}", LogType.Debug);
                        if (this.IsConnected)
                        {
                            this.Client.Send(data);
                            Thread.Sleep(TIME_SLEEP_SEND);
                        }
                    }


                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Error when send command at {ServerIP} to node 31!", LogType.Debug);
            }
            finally
            {
                _TimerPingCommand.Start();
            }
        }


        private void _TimerProccessSendCommand_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _TimerSendCommand.Stop();
                DateTime eventTime = DateTime.Now;
                byte _in1 = 0, _in2 = 0, _in3 = 0, _in4 = 0, _in5 = 0, _in6 = 0;
                //string _msg = "";
                //Sau khi nhận được, gửi đi ==> Xử lý thằng đầu chuyền.
                for (int _id = 1; _id <= NumberOfNodes; _id++)
                {
                    Andon_MSG nodeMsg = Nodes[_id];
                    if (nodeMsg.Body.In01 == 1) _in1 = 1;
                    if (nodeMsg.Body.In02 == 1) _in2 = 1;
                    //if (nodeMsg.Body.In03 == 1) _in3 = 1;
                    //if (nodeMsg.Body.In04 == 1) _in4 = 1;
                    //if (nodeMsg.Body.In05 == 1) _in5 = 1;
                    //if (nodeMsg.Body.In06 == 1) _in6 = 1;

                    //Send to Node for Light STOP
                    byte[] valueStop = { 0, (byte)nodeMsg.Body.In01 };
                    byte[] dataNodeStop = ModbusRTUOverTCP.WriteSingleRegisterMsg((byte)_id, 11, 6, valueStop);
                    string _msgStop = ByteArrayToString(dataNodeStop);
                    byte[] dataSendStop = Encoding.ASCII.GetBytes(_msgStop);

                    //Send to Node for Light BREAK
                    byte[] valueBreak = { 0, (byte)nodeMsg.Body.In02 };
                    byte[] dataNodeBreak = ModbusRTUOverTCP.WriteSingleRegisterMsg((byte)_id, 12, 6, valueBreak);
                    string _msgBreak = ByteArrayToString(dataNodeBreak);
                    byte[] dataSendBreak = Encoding.ASCII.GetBytes(_msgBreak);

                    _Logger.Write(_LogCategory, $"Starting send command at {ServerIP} to Local node {_id}: {_msgStop} && {_msgBreak}!", LogType.Debug);
                    if (this.IsConnected)
                    {
                        this.Client.Send(dataSendStop);
                        Thread.Sleep(TIME_SLEEP_SEND);
                        this.Client.Send(dataSendBreak);
                        Thread.Sleep(TIME_SLEEP_SEND);
                        _Logger.Write(_LogCategory, $"Finished send command at {ServerIP} to Local node {_id}!", LogType.Debug);
                    }
                }

                //Send to EndOfLine Node
                //byte[] value = { 0, 0, 0, _in1 };
                //byte[] data = ModbusRTUOverTCP.WriteSingleRegisterMsg(31, 11, 6, value);
                //_msg = new AndonMessage().PackageWrite(31, 11, _in1);

                string _msgLineStop = new AndonMessage().PackageWrite(31, 11, _in1);
                _msgLineStop = ProcessWriteCommand(_msgLineStop, _in1);
                byte[] dataLineStop = Encoding.ASCII.GetBytes(_msgLineStop);
                string _msgLineBreak = new AndonMessage().PackageWrite(31, 12, _in2);
                _msgLineBreak = ProcessWriteCommand(_msgLineBreak, _in2);
                byte[] dataLineBreak = Encoding.ASCII.GetBytes(_msgLineBreak);
                _Logger.Write(_LogCategory, $"Starting send command at {ServerIP} to node 31: {_msgLineStop} && {_msgLineBreak}!", LogType.Debug);
                if (this.IsConnected)
                {
                    this.Client.Send(dataLineBreak);
                    Thread.Sleep(TIME_SLEEP_SEND);
                    if (_in2 == 0)
                    {
                        this.Client.Send(dataLineStop);
                        Thread.Sleep(TIME_SLEEP_SEND);
                    }
                    _Logger.Write(_LogCategory, $"Finished send command at {ServerIP} to node 31!", LogType.Debug);
                }


            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Error when send command at {ServerIP} to node 31!", LogType.Debug);
            }
            finally
            {
                _TimerSendCommand.Start();

            }
        }

        private void _TimerReconnect_Elapsed(object sender, ElapsedEventArgs e)
        {
            _TimerReconnect.Stop();

            ReConnect();

            _TimerReconnect.Start();
        }


        private void ConnectRabbitMQ()
        {
            try
            {
                _EventBus = RabbitHutch.CreateBus($"host={_RabbitMQHost};virtualHost={_RabbitMQVirtualHost};username={_RabbitMQUser};password={_RabbitMQPassword}");
                if (_EventBus != null && _EventBus.IsConnected)
                {
                    _Logger.Write(_LogCategory, $"Client {this.ServerIP} connected to RabbitMQ!", LogType.Info);
                }

            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }
        //public bool SendDataToDevice(string message)
        //{
        //    bool isSendSuccess = false;
        //    try
        //    {
        //        if (!this.IsConnected)
        //        {
        //            this.Connect(remoteEndpoint);
        //            Thread.Sleep(100);
        //        }

        //        if (this.IsConnected)
        //        {
        //            byte[] messageByte = Encoding.ASCII.GetBytes(message);
        //            this.Send(messageByte, 0, message.Length);
        //            isSendSuccess = true;
        //        }
        //        else
        //        {
        //            ReConnect();
        //            byte[] messageByte = Encoding.ASCII.GetBytes(message);
        //            this.Send(messageByte, 0, message.Length);

        //            //log.Info("TCPSocket send data: " + message);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        isSendSuccess = false;
        //    }

        //    return isSendSuccess;
        //    //try
        //    //{
        //    //    this.Close();
        //    //}
        //    //catch { }

        //}

        void Connect()
        {
            try
            {
                lock (lockConnectSync)
                {
                    if (!this.IsConnected)
                    {
                        this.Connect(remoteEndpoint);
                    }
                    Thread.Sleep(TIME_WAIT_CONNECT);
                    _TimerPingCommand.Start();
                    _TimerSendCommand.Start();

                    //Khởi tạo nó tính từ đó là nhận dữ liệu
                    LastTimeReceiveData = DateTime.Now;

                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Error when Connect to {ServerIP}:{ex}", LogType.Error);
            }

        }
        void ReConnect()
        {
            _Logger.Write(_LogCategory, $"Reconnecting to gateway {ServerIP}...!", LogType.Debug);

            lock (lockConnectSync)
            {
                try
                {
                    Close();
                    Thread.Sleep(TIME_WAIT_CONNECT);
                }
                catch { }
                try
                {
                    Connect();
                }
                catch(Exception ex) {
                    _Logger.Write(_LogCategory, $"Error when ReConnect to {ServerIP}:{ex}", LogType.Error);
                }
            }

            //CountForNoData++;

        }

        public void OpenConnectLoop()
        {
            try
            {
                Task.Factory.StartNew(() =>
                {
                    while (isRunning)
                    {
                        if (!this.IsConnected)
                        {
                            //_Logger.Write(_LogCategory, $"Connecting to gateway {ServerIP}...!", LogType.Debug);
                            Connect();

                        }
                        Thread.Sleep(TIME_WAIT_CONNECT);

                        lock (lockReceiveData)
                        {
                            double timeDurationReceiveData = (DateTime.Now - LastTimeReceiveData).TotalSeconds;
                            if (timeDurationReceiveData > TIME_NOT_RECEIVE_DATA)
                            {
                                ReConnect();
                            }
                        }
                    }

                });
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Error when open connect loop: {ex}", LogType.Error);

                //Cố gắng kết nối lại
                ReConnect();
            }

        }

        public void CloseConnect()
        {
            isRunning = false;
            try
            {
                if (_EventBus != null)
                {
                    _EventBus.Dispose();
                }

                Close();
                Thread.Sleep(TIME_WAIT_CONNECT);
                //Thread.Sleep(500); //chờ 500ms mới quit app để đảm bảo thiết bị close đúng cách
            }
            catch { }
        }



        public void SessionOnDataReceived(object sender, DataEventArgs e)
        {
            try
            {


                byte[] message = e.Data;
                int messageSize = e.Length;

                Array.Resize(ref message, messageSize);
                string strMessage = Encoding.ASCII.GetString(message);
                //Console.WriteLine(strMessage);

                if (!strMessage.Contains(":"))
                    return;

                strMessage = strMessage.Replace("\0", ""); //Bỏ ký tự NULL
                strMessage = strMessage.Replace("\r\n", ""); //Bỏ ký tự xuống dòng

                _Logger.Write(_LogCategory, $"Received from IP {ServerIP}: [{strMessage}]", LogType.Debug);

                /*
                if (ServerIP == "172.17.125.24")
                {
                    //strMessage = strMessage.Replace("NUL", "");
                    //strMessage = strMessage.Replace(" ", "");
                    _Logger.Write(_LogCategory, $"Fixed from IP {ServerIP}: [{strMessage}]", LogType.Debug);
                }
                */

                //thoi gian nhan ban tin
                lock (lockReceiveData)
                {
                    LastTimeReceiveData = DateTime.Now;
                }
                //strMessage = strAvCache + strMessage;
                ////Xong thì xử lý cache
                //strAvCache = "";

                string[] separatingStrings = { ":" };
                string[] msgArry = strMessage.Split(separatingStrings, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < msgArry.Length; i++)
                {

                    string msgProcess = msgArry[i];

                    //===============================================================================
                    //AnNH: Thêm đoạn này để kiểm tra xem bản tin đã đủ độ dài chưa
                    if (msgProcess.Length < _MessageLength) //Chưa đủ độ dài của msg
                    {
                        //strAvCache = msgArry[i];
                        //log.Info(string.Format("Cache: {0}", strAvCache));
                        continue;
                    }

                    //===============================================================================
                    ////For testing. Test xong nhớ comment lại dòng này và mở ra dòng bên dưới.
                    //OnReceivedMessage?.Invoke(strMessage);

                    //log.Info(string.Format("publish to rabbit: {0}", msgProcess));
                    // rabbit.Publish(msgProcess);

                    //Bỏ qua bản tin trả về khi gửi lệnh ghi
                    string _command = msgProcess.Substring(2, 2);
                    if (_command == "06") continue;

                    _Logger.Write(_LogCategory, $"[{msgProcess}]", LogType.Debug);
                    ProcessLogic(msgProcess);

                }

            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Process message received from  {this.ServerIP} Error: {ex}!", LogType.Error);
            }


        }

        private Andon_MSG ParseModbusMessage(string data)
        {
            if (data.Length < 14)
            {
                _Logger.Write(_LogCategory, $"Error Messgage Received: {data}", LogType.Debug);
            }

            string _deviceId = data.Substring(0, 2);
            if (_deviceId == "00") return null;

            int _in1 = int.Parse(data.Substring(6, 4));
            int _in2 = int.Parse(data.Substring(10, 4));
            //if (_IsInvertInput)
            //{
            //    //_in1 = (_in1 == 0) ? 1 : 0;
            //    //_in2 = (_in2 == 0) ? 1 : 0;
            //}
            int _in3 = 0;//int.Parse(data.Substring(14, 4));
            int _in4 = 0;// int.Parse(data.Substring(12, 2), System.Globalization.NumberStyles.HexNumber);
            int _in5 = 0;// int.Parse(data.Substring(14, 2), System.Globalization.NumberStyles.HexNumber);
            int _in6 = 0;// int.Parse(data.Substring(16, 2), System.Globalization.NumberStyles.HexNumber);

            Andon_MSG msg = new Andon_MSG(this.ServerIP, DateTime.Now, MessageType.Andon, _deviceId, _in1, _in2, _in3, _in4, _in5, _in6);

            return msg;

        }
        /*
        private void ProcessResetReceived(string content)
        {
            try
            {
                //Kiểm tra xem nhận lệnh reset từ Node nào
                if (content.Length < 6) return;
                int _nodeId = Int32.Parse(content.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                //int _code = Int32.Parse(content.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                //_Logger.Write(_LogCategory, $"Checking is content of process reset received {_nodeId} from {this.ServerIP} with code = {_code}!", LogType.Debug);
                //Xác nhận tín hiệu Reset đã OK
                _Logger.Write(_LogCategory, $"Reset Node {_nodeId} from {this.ServerIP} done!", LogType.Info);
                //Cứ lệnh của Node nào bắn về chứng tỏ Node đó đã reset xong --> Xóa bỏ khỏi mảng Node2Reset
                ResetNode node = NodesReset.Find(n => n.NodeId == _nodeId);
                NodesReset.Remove(node);

            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Process reset message {content} from {this.ServerIP} Error: {ex}!", LogType.Debug);
            }

            return;
        }
        */

        public void ProcessLogic(string _rawMessage)
        {
            //string _rawMessage = JsonConvert.SerializeObject(message);
            try
            {
                //Phân tích msg ở đây

                Andon_MSG message = ParseModbusMessage(_rawMessage);
                if (message == null) return;

                Nodes[int.Parse(message.Body.DeviceId)] = message;
                _Logger.Write(_LogCategory, $"{JsonConvert.SerializeObject(message)}", LogType.Debug, this.ServerIP);

                // publish
                if (_EventBus == null)
                {
                    // try connect to rabbitmq
                    ConnectRabbitMQ();
                }

                if (!_EventBus.IsConnected)
                {
                    // try connect to rabbitmq
                    ConnectRabbitMQ();
                }

                if (_EventBus != null && _EventBus.IsConnected)
                {
                    _EventBus.Publish<Andon_MSG>(message);
                }
                else
                {
                    _Logger.Write(_LogCategory, $" [{_rawMessage}]", LogType.Error, "_Error_" + this.ServerIP);
                }
                //Ghi logs raws
                //_Logger.Write(_LogCategory, $" [{_rawMessage}]", LogType.Debug, this.ServerIP);

            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"Process logic message {_rawMessage} from  {this.ServerIP} Error: {ex}!", LogType.Error);
            }

        }

        private string ProcessWriteCommand(string msg, byte in01)
        {
            string ret = msg;
            if (in01 == 1)
            {
                ret += "CF";
            }
            else
            {
                ret += "D0";
            }
            if (!ret.StartsWith(":")) ret = ":" + ret;
            if (!ret.EndsWith("\r\n")) ret += "\r\n";
            
            return ret;
        }
        private string ByteArrayToString(byte[] ba)
        {
            string ret = "";
            for(int _id = 0; _id < ba.Length; _id++)
            {
                ret += $"{ba[_id]:X2}";
            }
            if (!ret.StartsWith(":")) ret = ":" + ret;
            if (!ret.EndsWith("\r\n")) ret += "\r\n";

            return ret;
        }
        private string ByteArrayToString(byte[] ba, int size)
        {
            //StringBuilder hex = new StringBuilder(ba.Length * 2);
            StringBuilder hex = new StringBuilder(size * 2);

            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        void SessionConnected(object sender, EventArgs e)
        {
            _Logger.Write(_LogCategory, $"Connected to {ServerIP}", LogType.Info);
        }

        void SessionClosed(object sender, EventArgs e)
        {
            _Logger.Write(_LogCategory, $"Session to {ServerIP} closed!", LogType.Info);
            
        }

        void SessionError(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            _Logger.Write(_LogCategory, $"Error: {e}", LogType.Error);
        }

        public override int ReceiveBufferSize { get => base.ReceiveBufferSize; set => base.ReceiveBufferSize = value; }

        public override void Close()
        {
            _TimerPingCommand.Stop();
            _TimerSendCommand.Stop();
            base.Close();
        }

        public override void Connect(EndPoint remoteEndpoint)
        {
            try
            {
                base.Connect(remoteEndpoint);
            }
            catch (Exception ex)
            {

            }
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override string ToString()
        {
            return base.ToString();
        }

        public override bool TrySend(ArraySegment<byte> segment)
        {
            return base.TrySend(segment);
        }

        public override bool TrySend(IList<ArraySegment<byte>> segments)
        {
            return base.TrySend(segments);
        }

        protected override bool IsIgnorableException(Exception e)
        {
            return base.IsIgnorableException(e);
        }

        protected override void OnClosed()
        {
            base.OnClosed();
        }

        protected override void OnConnected()
        {
            base.OnConnected();
        }

        protected override void OnDataReceived(byte[] data, int offset, int length)
        {
            base.OnDataReceived(data, offset, length);
        }

        protected override void OnError(Exception e)
        {
            base.OnError(e);
            _Logger.Write(_LogCategory, $"Error: {e}", LogType.Error);

        }

        protected override void OnGetSocket(SocketAsyncEventArgs e)
        {
            base.OnGetSocket(e);
        }

        protected override void SendInternal(PosList<ArraySegment<byte>> items)
        {
            base.SendInternal(items);
        }

        protected override void SetBuffer(ArraySegment<byte> bufferSegment)
        {
            base.SetBuffer(bufferSegment);
        }

        protected override void SocketEventArgsCompleted(object sender, SocketAsyncEventArgs e)
        {
            base.SocketEventArgsCompleted(sender, e);
        }
    }
}
