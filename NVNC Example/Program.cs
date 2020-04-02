#region

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NVNC;
using Renci.SshNet;
using Renci.SshNet.Common;

#endregion

namespace VNCTest
{
    internal class Program
    {
        public static void startTunnel()
        {
            var keypath = @"";
            var pk = new PrivateKeyFile(keypath);
            var keyFiles = new[] { pk };

            var client = new SshClient("server.com", 22, "user", pk);
            client.KeepAliveInterval = new TimeSpan(0, 0, 30);
            client.ConnectionInfo.Timeout = new TimeSpan(0, 0, 20);
            try
            {
                client.Connect();
                Console.WriteLine("Connecté à server");
            }
            catch
            {
                Console.WriteLine("Erreur de connexion au tunnel");
            }
            IPAddress iplocal = IPAddress.Parse("127.0.0.1");
            IPAddress ipremote = IPAddress.Parse("ipserver");
            UInt32 portdistant = 22;
            UInt32 portlocal = 25;
            ForwardedPortRemote port = new ForwardedPortRemote(ipremote, portdistant, iplocal, portlocal);
            client.AddForwardedPort(port);
            port.Exception += delegate (object sender, ExceptionEventArgs e)
            {
                Console.WriteLine(e.Exception.ToString());
            };
            port.Start();
            Console.WriteLine("Port forwarding ok");
            System.Threading.Thread.Sleep(1000 * 60 * 60 * 8);
            port.Stop();
            client.Disconnect();

        }
        private static void Main(string[] args)
        {
            
            var s = new VncServer("", "a", 5901, 5900, "Ulterius VNC");
            Thread thr = new Thread(new ThreadStart(startTunnel));
            
            try
            {
                thr.Start();
                s.Start();
                Console.Read();
                s.Stop();
            }
            catch (ArgumentNullException ex)
            {
               s.Stop();
                return;
            }
            
        }



    }
}