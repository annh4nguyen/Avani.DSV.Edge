using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using Avani.Helper;
using System.Configuration;
using EasyNetQ;
using iAndon.MSG;
//using Avani.Andon.Resources;

namespace Avani.Andon.Edge.Logic
{
    public class TCPServer
    {
        private Log _Logger;
        private readonly string _LogCategory = "TCPServer";

        public static int ServerPort;
        public static IPEndPoint EndPoint;

        /// <summary>
        /// Local Variables Declaration.
        /// </summary>
        private string _RabbitMQHost = ConfigurationManager.AppSettings["RabbitMQ.Host"];
        private string _RabbitMQVirtualHost = ConfigurationManager.AppSettings["RabbitMQ.VirtualHost"];
        private string _RabbitMQUser = ConfigurationManager.AppSettings["RabbitMQ.User"];
        private string _RabbitMQPassword = ConfigurationManager.AppSettings["RabbitMQ.Password"];
        private string _CustomerID = ConfigurationManager.AppSettings["CustomerID"];

        private int _QueueInterval = 1000 * int.Parse(ConfigurationManager.AppSettings["queue_interval"]);
        private System.Timers.Timer _TimerProccessQueue = new System.Timers.Timer();


        private TcpListener _Server = null;
        private bool _StopServer = false;
        private bool _StopPurging = false;
        private Thread _ServerThread = null;
        private Thread _PurgingThread = null;
        private ArrayList _ListenersList = null;
        private IBus _EventBus;
        /// <summary>
        /// Constructors.
        /// </summary>
        public TCPServer(int serverPort)
        {
            try
            {
                _Logger = Avani.Andon.Edge.Logic.Helper.GetLog();
                ServerPort = serverPort;
                EndPoint = new IPEndPoint(IPAddress.Any, ServerPort);
                Init(EndPoint);
            }
            catch(Exception ex)
            {
                throw ex;
            }
        }
        /// <summary>
        /// Destructor.
        /// </summary>
        ~TCPServer()
        {
            try
            {
                Stop();
            }
            catch(Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }

        /// <summary>
        /// Init method that create a server (TCP Listener) Object based on the
        /// IP Address and Port information that is passed in.
        /// </summary>
        /// <param name="endPoint"></param>
        private void Init(IPEndPoint endPoint)
        {
            try
            {
                _Server = new TcpListener(endPoint);
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
                _Server = null;
            }
        }

        /// <summary>
        /// Method that starts TCP/IP Server.
        /// </summary>
        public void Start()
        {
            try
            {
                _Logger.Write(_LogCategory, "Server Start", LogType.Info);
                if (_Server != null)
                {
                    // Create a ArrayList for storing SocketListeners before starting the server.
                    _ListenersList = new ArrayList();

                    // Start the Server and start the thread to listen client requests.
                    _Server.Start();
                    _ServerThread = new Thread(new ThreadStart(ServerThreadStart));
                    _ServerThread.Start();

                    // Create a low priority thread that checks and deletes client
                    // SocktConnection objcts that are marked for deletion.
                    _PurgingThread = new Thread(new ThreadStart(PurgingThreadStart));
                    _PurgingThread.Priority = ThreadPriority.Lowest;
                    _PurgingThread.Start();


                    _TimerProccessQueue.Interval = _QueueInterval;
                    _TimerProccessQueue.Elapsed += _TimerProccessQueue_Elapsed;
                    _TimerProccessQueue.Start();


                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }


        /// <summary>
        /// Method that stops the TCP/IP Server.
        /// </summary>
        public void Stop()
        {
            try
            {
                if (_Server != null)
                {
                    // Stop the TCP/IP Server.
                    _StopServer = true;
                    _Server.Stop();

                    if (_EventBus != null)
                    {
                        _Logger.Write(_LogCategory, $"Disconnect RabbitMQ", LogType.Info);
                        _EventBus.Dispose();
                        _EventBus = null;
                    }
                    if (_ServerThread != null)
                    {
                        // Wait for one second for the the thread to stop.
                        _ServerThread.Join(1000);

                        // If still alive; Get rid of the thread.
                        if (_ServerThread.IsAlive)
                        {
                            _ServerThread.Abort();
                        }
                        _ServerThread = null;
                    }

                    _StopPurging = true;
                    _PurgingThread.Join(1000);
                    if (_PurgingThread.IsAlive)
                    {
                        _PurgingThread.Abort();
                    }
                    _PurgingThread = null;

                    // Free Server Object.
                    _Server = null;

                    // Stop All clients.
                    StopAllSocketListers();
                    _Logger.Write(_LogCategory, "Server Stoped", LogType.Info);
                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }

        /// <summary>
        /// Method that stops all clients and clears the list.
        /// </summary>
        private void StopAllSocketListers()
        {
            try
            {
                foreach (TCPSocketListener listener in _ListenersList)
                {
                    listener.StopSocketListener();
                }
                // Remove all elements from the list.
                _ListenersList.Clear();
                _ListenersList = null;
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }

        /// <summary>
        /// TCP/IP Server Thread that is listening for clients.
        /// </summary>
        private void ServerThreadStart()
        {
            try
            {
                while (!_StopServer)
                {
                    try
                    {
                        // Client Socket variable;
                        TcpClient tcpClient = _Server.AcceptTcpClient();
                        TCPSocketListener listener = new TCPSocketListener(tcpClient);

                        lock (_ListenersList)
                        {
                            _ListenersList.Add(listener);
                        }

                        listener.StartSocketListener();
                    }
                    catch (SocketException se)
                    {
                        _Logger.Write(_LogCategory, $"Server Start Error: {se.Message}", LogType.Error);
                        _StopServer = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }

        private void PurgingThreadStart()
        {
            try
            {
                while (!_StopPurging)
                {
                    ArrayList deleteList = new ArrayList();

                    //Monitor.Enter(_ListenersList);
                    lock (_ListenersList)
                    {
                        foreach (TCPSocketListener listener in _ListenersList)
                        {
                            if (listener.IsMarkedForDeletion())
                            {
                                deleteList.Add(listener);
                                listener.StopSocketListener();
                            }
                        }
                        if (deleteList.Count > 0)
                        {
                            _Logger.Write(_LogCategory, $"Stop {deleteList.Count}/{_ListenersList.Count} Listeners", LogType.Debug);
                        }
                        // Delete all the client SocketConnection ojects which are in marked for deletion and are in the delete list.
                        for (int i = 0; i < deleteList.Count; ++i)
                        {
                            _ListenersList.Remove(deleteList[i]);
                        }
                    }
                    //Monitor.Exit(_ListenersList);
                    deleteList = null;
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }


        private void _TimerProccessQueue_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            System.Timers.Timer timer = sender as System.Timers.Timer;
            timer.Stop();
            try
            {
                StartGetMessage();
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, $"ProccessQueue Error: {ex}", LogType.Error);
            }
            finally
            {
                timer.Start();
            }
        }

        private void StartGetMessage()
        {
            try
            {
                _Logger.Write(_LogCategory, $"Start Get Messages from Rabbit", LogType.Info);

                if (_EventBus == null || !_EventBus.IsConnected || !_EventBus.Advanced.IsConnected)
                {
                    _Logger.Write(_LogCategory, $"Connecting to RabbitMQ {_RabbitMQHost}", LogType.Info);
                    _EventBus = RabbitHutch.CreateBus($"host={_RabbitMQHost};virtualHost={_RabbitMQVirtualHost};username={_RabbitMQUser};password={_RabbitMQPassword}");
                    if (_EventBus.IsConnected || _EventBus.Advanced.IsConnected)
                    {
                        _Logger.Write(_LogCategory, $"Connected to RabbitMQ {_RabbitMQHost}", LogType.Info);
                    }
                }
                _EventBus.Subscribe<DST_MSG>(_CustomerID, msg => {
                    ProcessMessage(msg);
                });
            }
            catch (Exception ex)
            {
                if (_EventBus != null) _EventBus.Dispose();
                _EventBus = null;
                _Logger.Write(_LogCategory, $"Start Get Message to Control Error: {ex}", LogType.Error);
            }
        }

        private void ProcessMessage(DST_MSG msg)
        {
            try
            {
                if (msg == null) return;

                lock (_ListenersList)
                {
                    foreach (TCPSocketListener listener in _ListenersList)
                    {
                        if (!listener.IsStopClient())
                        {
                            string strMessage = new AndonMessage().PackageSetValueMessage(1, msg.Body.NodeId, msg.Body.In01, msg.Body.In02, msg.Body.In03, 0, 0, 0);
                            listener.SendCommand(strMessage);

                            _Logger.Write(_LogCategory, $"Send command: {strMessage}", LogType.Debug);

                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _Logger.Write(_LogCategory, ex);
            }
        }

    }
}
