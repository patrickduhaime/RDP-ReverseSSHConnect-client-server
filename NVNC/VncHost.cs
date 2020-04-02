#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Forms;
using NVNC.Encodings;
using NVNC.Readers;
using NVNC.Utils;
using NVNC.Writers;

#endregion

namespace NVNC
{
    /// <summary>
    ///     Contains methods and properties to handle all aspects of the RFB Protocol versions 3.3 - 3.8.
    /// </summary>
    public class VncHost
    {
        /// <summary>
        ///     Client to Server Message-Type constants.
        /// </summary>
        public enum ClientMessages : byte
        {
            SetPixelFormat = 0,
            ReadColorMapEntries = 1,
            SetEncodings = 2,
            FramebufferUpdateRequest = 3,
            KeyEvent = 4,
            PointerEvent = 5,
            ClientCutText = 6
        }

        /// <summary>
        ///     RFB Encoding constants.
        /// </summary>
        public enum Encoding
        {
            //encodings
            RawEncoding = 0, //working
            CopyRectEncoding = 1, //working
            RreEncoding = 2, //working
            CoRreEncoding = 4, //error
            HextileEncoding = 5, //working
            ZrleEncoding = 16, //working

            //pseudo-encodings
            ZlibEncoding = 6 //working
        }

        /// <summary>
        ///     Server to Client Message-Type constants.
        /// </summary>
        public enum ServerMessages
        {
            FramebufferUpdate = 0,
            SetColorMapEntries = 1,
            Bell = 2,
            ServerCutText = 3
        }

        private readonly HashSet<Socket> _clients = new HashSet<Socket>();

        public string CutText;

        public string DisplayName;

        public bool isRunning;
        protected Socket localClient; // Network object used to communicate with host

        // TODO: this color map code should probably go in Framebuffer.cs
        protected BinaryReader reader; // Integral rather than Byte values are typically

        public ScreenHandler screenHandler;
        protected TcpListener serverSocket;

        protected NetworkStream stream; // Stream object used to send/receive data

        //Version numbers
        protected int verMajor = 3; // Major version of Protocol--probably 3
        protected int verMinor = 8; //8; // Minor version of Protocol--probably 3, 7, or 8
        protected BinaryWriter writer; // sent and received, so these handle this.
        protected ZlibCompressedWriter zlibWriter; //The Zlib Stream Writer used for Zlib and ZRLE encodings


        public VncHost(int port, string displayname, ScreenHandler sc)
        {
            Port = port;
            DisplayName = displayname;
            screenHandler = sc;
            Start();
        }

        public bool KillIfDc()
        {
            return _clients.Any(client => !client.Connected);
        }

        //Shared flag
        public bool Shared { get; set; }

        //Port property
        public int Port { get; set; }

        public ICollection<Socket> Clients
        {
            get { return _clients; }
        }

        //Supported encodings
        public uint[] Encodings { get; private set; }

        /// <summary>
        ///     Gets the Protocol Version of the remote VNC Host--probably 3.3, 3.7, or 3.8.
        /// </summary>
        /// <returns>Returns a float representation of the protocol version that the server is using.</returns>
        public float ServerVersion
        {
            get { return verMajor + verMinor*0.1f; }
        }

        public ZlibCompressedWriter ZlibWriter
        {
            get { return zlibWriter; }
        }

        public ushort[,] MapEntries { get; } = new ushort[256, 3];

        /// <summary>
        ///     Gets the best encoding from the ones the client sent.
        /// </summary>
        /// <returns>Returns a enum representation of the encoding.</returns>
        public Encoding GetPreferredEncoding()
        {
            //TODO: If our preferred encoding is not found, use the best one the client sent
            var prefEnc = Encoding.RawEncoding;
            try
            {
                if (Encodings.Any(t => (Encoding) t == prefEnc))
                {
                    return prefEnc;
                }
            }
            catch
            {
                prefEnc = Encoding.RawEncoding;
            }
            return prefEnc;
        }

        /// <summary>
        ///     The main server loop. Listening on the selected port occurs here, and accepting incoming connections
        /// </summary>
        public void Start()
        {
            isRunning = true;
            try
            {
                serverSocket = new TcpListener(IPAddress.Any, Port);
                serverSocket.Server.NoDelay = true;
                serverSocket.Start();
            }
                //The port is being used, and serverSocket cannot start
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                //Close();
                return;
            }
            try
            {
                //SocketError error = SocketError.AccessDenied;

                localClient = serverSocket.AcceptSocket();
                localClient.NoDelay = true; //Disable the Naggle algorithm

                var localIP = IPAddress.Parse(((IPEndPoint) localClient.RemoteEndPoint).Address.ToString());
                Console.WriteLine(localIP);

                stream = new NetworkStream(localClient, true);
                reader = new BigEndianBinaryReader(stream);
                writer = new BigEndianBinaryWriter(stream);
                zlibWriter = new ZlibCompressedWriter(stream);
                _clients.Add(localClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        ///     Reads VNC Protocol Version message (see RFB Doc v. 3.8 section 6.1.1)
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown if the version of the protocol is not known or supported.</exception>
        public void ReadProtocolVersion()
        {
            try
            {
                var b = reader.ReadBytes(12);
                // As of the time of writing, the only supported versions are 3.3, 3.7, and 3.8.
                if (b[0] == 0x52 && // R
                    b[1] == 0x46 && // F
                    b[2] == 0x42 && // B
                    b[3] == 0x20 && // (space)
                    b[4] == 0x30 && // 0
                    b[5] == 0x30 && // 0
                    b[6] == 0x33 && // 3
                    b[7] == 0x2e && // .
                    (b[8] == 0x30 || // 0
                     b[8] == 0x38) && // BUG FIX: Apple reports 8 
                    (b[9] == 0x30 || // 0
                     b[9] == 0x38) && // BUG FIX: Apple reports 8 
                    (b[10] == 0x33 || // 3, 7, OR 8 are all valid and possible
                     b[10] == 0x36 || // BUG FIX: UltraVNC reports protocol version 3.6!
                     b[10] == 0x37 ||
                     b[10] == 0x38 ||
                     b[10] == 0x39) && // BUG FIX: Apple reports 9					
                    b[11] == 0x0a) // \n
                {
                    // Since we only currently support the 3.x protocols, this can be assumed here.
                    // If and when 4.x comes out, this will need to be fixed--however, the entire 
                    // protocol will need to be updated then anyway :)
                    verMajor = 3;
                    // Figure out which version of the protocol this is:
                    switch (b[10])
                    {
                        case 0x33:
                        case 0x36: // BUG FIX: pass 3.3 for 3.6 to allow UltraVNC to work, thanks to Steve Bostedor.
                            verMinor = 3;
                            break;
                        case 0x37:
                            verMinor = 7;
                            break;
                        case 0x38:
                            verMinor = 8;
                            break;
                        case 0x39: // BUG FIX: Apple reports 3.889
                            // According to the RealVNC mailing list, Apple is really using 3.3 
                            // (see http://www.mail-archive.com/vnc-list@realvnc.com/msg23615.html).  I've tested with
                            // both 3.3 and 3.8, and they both seem to work (I obviously haven't hit the issues others have).
                            // Because 3.8 seems to work, I'm leaving that, but it might be necessary to use 3.3 in future.
                            verMinor = 8;
                            break;
                    }
                }
                else
                {
                    throw new NotSupportedException("Only versions 3.3, 3.7, and 3.8 of the RFB Protocol are supported.");
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Sends the Protocol Version supported by the server.  Will be highest supported by client (see RFB Doc v. 3.8
        ///     section 6.1.1).
        /// </summary>
        public void WriteProtocolVersion()
        {
            try
            {
                // We will use which ever version the server understands, be it 3.3, 3.7, or 3.8.
                Debug.Assert(verMinor == 3 || verMinor == 7 || verMinor == 8, "Wrong Protocol Version!",
                    string.Format("Protocol Version should be 3.3, 3.7, or 3.8 but is {0}.{1}", verMajor, verMinor));

                writer.Write(GetBytes(string.Format("RFB 003.00{0}\n", verMinor)));
                writer.Flush();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Closes the active connection and stops the VNC Server.
        /// </summary>
        public void Close()
        {
            Console.WriteLine("Stopping VNC Server");
            isRunning = false;
            serverSocket.Stop();
            Console.WriteLine("Socket stopped for VNC");
            if (localClient.Connected)
                localClient.Disconnect(true);
        }

        /// <summary>
        ///     Converts the VNC password to a byte array.
        /// </summary>
        /// <param name="password">The VNC password as a String, to be converted to bytes.</param>
        /// <returns>Returns a byte array of the password.</returns>
        private byte[] PasswordToKey(string password)
        {
            var key = new byte[8];
            // Key limited to 8 bytes max.
            if (password.Length >= 8)
            {
                System.Text.Encoding.ASCII.GetBytes(password, 0, 8, key, 0);
            }
            else
            {
                System.Text.Encoding.ASCII.GetBytes(password, 0, password.Length, key, 0);
            }
            // VNC uses reverse byte order in key
            for (var i = 0; i < 8; i++)
                key[i] = (byte) (((key[i] & 0x01) << 7) |
                                 ((key[i] & 0x02) << 5) |
                                 ((key[i] & 0x04) << 3) |
                                 ((key[i] & 0x08) << 1) |
                                 ((key[i] & 0x10) >> 1) |
                                 ((key[i] & 0x20) >> 3) |
                                 ((key[i] & 0x40) >> 5) |
                                 ((key[i] & 0x80) >> 7));
            return key;
        }

        /// <summary>
        ///     If the password is not empty, perform VNC Authentication with it.
        /// </summary>
        /// <param name="password">The current VNC Password</param>
        /// <returns>Returns a boolean value representing successful authentication or not.</returns>
        public bool WriteAuthentication(string password)
        {
            // Indicate to the client which type of authentication will be used.
            //The type of Authentication to be used, 1 (None) or 2 (VNC Authentication).
            if (string.IsNullOrEmpty(password))
            {
                // Protocol Version 3.7 onward supports multiple security types, while 3.3 only 1
                if (verMinor == 3)
                {
                    WriteUint32(1);
                }
                else
                {
                    byte[] types = {1};
                    writer.Write((byte) types.Length);

                    foreach (var t in types)
                        writer.Write(t);
                }
                if (verMinor >= 7)
                    reader.ReadByte();
                if (verMinor == 8)
                    WriteSecurityResult(0);
                return true;
            }
            if (verMinor == 3)
            {
                WriteUint32(2);
            }
            else
            {
                byte[] types = {2};
                writer.Write((byte) types.Length);

                foreach (var t in types)
                    writer.Write(t);
            }
            if (verMinor >= 7)
                reader.ReadByte();

            //A random 16 byte challenge
            var bChallenge = new byte[16];
            var rand = new Random(DateTime.Now.Millisecond);
            rand.NextBytes(bChallenge);

            // send the bytes to the client and wait for the response
            writer.Write(bChallenge);
            writer.Flush();

            var receivedBytes = reader.ReadBytes(16);
            var key = PasswordToKey(password);

            DES des = new DESCryptoServiceProvider();
            des.Padding = PaddingMode.None;
            des.Mode = CipherMode.ECB;
            var enc = des.CreateEncryptor(key, null);
            var ourBytes = new byte[16];
            enc.TransformBlock(bChallenge, 0, bChallenge.Length, ourBytes, 0);

            /*
                Console.WriteLine("Us: " + System.Text.Encoding.ASCII.GetString(ourBytes));
                Console.WriteLine("Client sent us: " + System.Text.Encoding.ASCII.GetString(receivedBytes));
                */
            var authOK = true;
            for (var i = 0; i < ourBytes.Length; i++)
                if (receivedBytes[i] != ourBytes[i])
                    authOK = false;

            if (authOK)
            {
                WriteSecurityResult(0);
                return true;
            }
            WriteSecurityResult(1);
            if (verMinor != 8) return false;
            var ErrorMsg = "Wrong password, sorry";
            WriteUint32((uint) ErrorMsg.Length);
            writer.Write(GetBytes(ErrorMsg));
            return false;
        }

        /// <summary>
        ///     When the client uses VNC Authentication, after the Challege/Response, a status code is sent to indicate whether
        ///     authentication worked.
        /// </summary>
        /// <param name="sr">
        ///     An unsigned integer indicating the status of authentication: 0 = OK; 1 = Failed; 2 = Too Many
        ///     (deprecated).
        /// </param>
        public void WriteSecurityResult(uint sr)
        {
            writer.Write(sr);
        }

        /// <summary>
        ///     Receives an Initialisation message from the client.
        /// </summary>
        /// <returns>True if the server allows other clients to connect, otherwise False.</returns>
        public bool ReadClientInit()
        {
            var sh = false;
            try
            {
                Shared = reader.ReadByte() == 1;
                sh = Shared;
                return Shared;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
            return sh;
        }

        /// <summary>
        ///     Writes the server's Initialization message, specifically the Framebuffer's properties.
        /// </summary>
        /// <param name="fb">The framebuffer that is sent.</param>
        public void WriteServerInit(Framebuffer fb)
        {
            try
            {
                writer.Write(Convert.ToUInt16(fb.Width));
                writer.Write(Convert.ToUInt16(fb.Height));
                writer.Write(fb.ToPixelFormat());

                writer.Write(Convert.ToUInt32(fb.DesktopName.Length));
                writer.Write(GetBytes(fb.DesktopName));
                writer.Flush();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Receives the format to be used when sending Framebuffer Updates.
        /// </summary>
        /// <returns>
        ///     A Framebuffer telling the server how to encode pixel data. Typically this will be the same one sent by the
        ///     server during initialization.
        /// </returns>
        public Framebuffer ReadSetPixelFormat(int w, int h)
        {
            Framebuffer ret = null;
            try
            {
                ReadPadding(3);
                var pf = ReadBytes(16);
                ret = Framebuffer.FromPixelFormat(pf, w, h);
                return ret;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
            return ret;
        }

        /// <summary>
        ///     Reads the supported encodings from the client.
        /// </summary>
        public void ReadSetEncodings()
        {
            try
            {
                ReadPadding(1);
                var len = reader.ReadUInt16();
                var enc = new uint[len];

                for (var i = 0; i < (int) len; i++)
                    enc[i] = reader.ReadUInt32();
                Encodings = enc;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Reads a request for an update of the area specified by (x, y, w, h).
        ///     <param name="fb">The server's current Framebuffer.</param>
        /// </summary>
        public void ReadFrameBufferUpdateRequest(Framebuffer fb)
        {
            try
            {
                var incremental = Convert.ToBoolean((int) reader.ReadByte());
                var x = reader.ReadUInt16();
                var y = reader.ReadUInt16();
                var width = reader.ReadUInt16();
                var height = reader.ReadUInt16();

                //Console.WriteLine("FrameBufferUpdateRequest on x: " + x + " y: " + y + " w: " + width + " h:" + height);

                /*new Thread(delegate() { */
                DoFrameBufferUpdate(fb, incremental, x, y, width, height);
                /*}).Start();*/
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Creates the encoded pixel data in a form of an EncodedRectangle with the preferred encoding.
        /// </summary>
        private void DoFrameBufferUpdate(Framebuffer fb, bool incremental, int x, int y, int width, int height)
        {
            //if (incremental)
            //    return;
            Trace.WriteLine("X: " + x + " Y: " + y + " W: " + fb.Width + " H: " + fb.Height);
            var w = fb.Width;
            var h = fb.Height;
            if ((x < 0) || (y < 0) || (width <= 0) || (height <= 0))
            {
                Trace.WriteLine("Neg:" + x + ":" + y + ":" + width + ":" + height);
                return;
            }
            if (x + width > w)
            {
                Trace.WriteLine("Too wide");
                return;
            }
            if (y + height > h)
            {
                Trace.WriteLine("Too high");
                return;
            }
            Trace.WriteLine("Bounds OK!");

            var rectangles = new HashSet<EncodedRectangle>();
            try
            {
                var tip = Stopwatch.StartNew();
                var factory = new EncodedRectangleFactory(this, fb);

                var localRect = factory.Build(new Rectangle2(x, y, width, height), GetPreferredEncoding());
                localRect.Encode();
                rectangles.Add(localRect);
                Trace.WriteLine("Encoding took: " + tip.Elapsed);
            }
            catch (Exception localException)
            {
                Console.WriteLine(localException.StackTrace);
                if (localException is IOException)
                {
                    Close();
                    return;
                }
            }
            if (rectangles.Count != 0)
                WriteFrameBufferUpdate(rectangles);
        }

        /// <summary>
        ///     Writes the number of update rectangles being sent to the client.
        ///     After that, for each rectangle, the encoded data is sent.
        /// </summary>
        public void WriteFrameBufferUpdate(ICollection<EncodedRectangle> rectangles)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                WriteServerMessageType(ServerMessages.FramebufferUpdate);
                WritePadding(1);
                writer.Write(Convert.ToUInt16(rectangles.Count));

                foreach (var e in rectangles)
                    e.WriteData();
                writer.Flush();

                watch.Stop();
                Trace.WriteLine("Sending took: " + watch.Elapsed);
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Receives a key press or release event from the client.
        /// </summary>
        public void ReadKeyEvent()
        {
            try
            {
                var pressed = reader.ReadByte() == 1;
                ReadPadding(2);
                var keysym = reader.ReadUInt32();

                //Do KeyEvent
                //new Thread(delegate() { 
                Robot.KeyEvent(pressed, Convert.ToInt32(keysym));
                //}).Start();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Receives a mouse movement or button press/release from the client.
        /// </summary>
        public void ReadPointerEvent()
        {
            try
            {
                var buttonMask = reader.ReadByte();
                var X = reader.ReadUInt16();
                var Y = reader.ReadUInt16();
                /*new Thread(delegate() { */
                Robot.PointerEvent(buttonMask, X, Y);
                /*}).Start();*/
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Receives the clipboard data from the client.
        /// </summary>
        public void ReadClientCutText()
        {
            /*
            try
            {
                ReadPadding(3);

                var len = Convert.ToInt32(reader.ReadUInt32());
                var text = GetString(reader.ReadBytes(len));
                CutText = text;
                Clipboard.SetDataObject(text.Replace("\n", Environment.NewLine), true);
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }*/

        }

        /// <summary>
        ///     Reads the type of message being sent by the client--all messages are prefixed with a message type.
        /// </summary>
        /// <returns>Returns the message type as an integer.</returns>
        public ClientMessages ReadServerMessageType()
        {
            byte x = 0;
            try
            {
                x = Convert.ToByte((int) reader.ReadByte());
                return (ClientMessages) x;
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
            return (ClientMessages) x;
        }

        /// <summary>
        ///     Writes the type of message being sent to the client--all messages are prefixed with a message type.
        /// </summary>
        private void WriteServerMessageType(ServerMessages message)
        {
            try
            {
                writer.Write(Convert.ToByte(message));
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Reads 8-bit RGB color values (or updated values) into the color map.
        /// </summary>
        public void ReadColorMapEntry()
        {
            ReadPadding(1);
            var firstColor = ReadUInt16();
            var nbColors = ReadUInt16();

            for (var i = 0; i < nbColors; i++, firstColor++)
            {
                MapEntries[firstColor, 0] = (byte) (ReadUInt16()*byte.MaxValue/ushort.MaxValue); // R
                MapEntries[firstColor, 1] = (byte) (ReadUInt16()*byte.MaxValue/ushort.MaxValue); // G
                MapEntries[firstColor, 2] = (byte) (ReadUInt16()*byte.MaxValue/ushort.MaxValue); // B
            }
        }

        /// <summary>
        ///     Writes 8-bit RGB color values (or updated values) from the color map.
        /// </summary>
        public void WriteColorMapEntry(ushort firstColor, Color[] colors)
        {
            try
            {
                WriteServerMessageType(ServerMessages.SetColorMapEntries);

                WritePadding(1);
                writer.Write(firstColor);
                writer.Write((ushort) colors.Length);

                foreach (var t in colors)
                {
                    writer.Write((ushort) t.R);
                    writer.Write((ushort) t.G);
                    writer.Write((ushort) t.B);
                }
                writer.Flush();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        /// <summary>
        ///     Writes the text from the Cut Buffer on the server.
        /// </summary>
        public void WriteServerCutText(string text)
        {
            try
            {
                WriteServerMessageType(ServerMessages.ServerCutText);
                WritePadding(3);

                writer.Write((uint) text.Length);
                writer.Write(GetBytes(text));
                writer.Flush();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                Close();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Here's all the "low-level" protocol stuff so user objects can access the data directly

        /// <summary>
        ///     Reads a single UInt32 value from the server, taking care of Big- to Little-Endian conversion.
        /// </summary>
        /// <returns>Returns a UInt32 value.</returns>
        public uint ReadUint32()
        {
            return reader.ReadUInt32();
        }

        /// <summary>
        ///     Reads a single UInt16 value from the server, taking care of Big- to Little-Endian conversion.
        /// </summary>
        /// <returns>Returns a UInt16 value.</returns>
        public ushort ReadUInt16()
        {
            return reader.ReadUInt16();
        }

        /// <summary>
        ///     Reads a single Byte value from the server.
        /// </summary>
        /// <returns>Returns a Byte value.</returns>
        public byte ReadByte()
        {
            return reader.ReadByte();
        }

        /// <summary>
        ///     Reads the specified number of bytes from the server, taking care of Big- to Little-Endian conversion.
        /// </summary>
        /// <param name="count">The number of bytes to be read.</param>
        /// <returns>Returns a Byte Array containing the values read.</returns>
        public byte[] ReadBytes(int count)
        {
            return reader.ReadBytes(count);
        }

        /// <summary>
        ///     Writes a single UInt32 value to the server, taking care of Little- to Big-Endian conversion.
        /// </summary>
        /// <param name="value">The UInt32 value to be written.</param>
        public void WriteUint32(uint value)
        {
            writer.Write(value);
        }

        /// <summary>
        ///     Writes a single unsigned short value to the server, taking care of Little- to Big-Endian conversion.
        /// </summary>
        /// <param name="value">The UInt16 value to be written.</param>
        public void WriteUInt16(ushort value)
        {
            writer.Write(value);
        }

        /// <summary>
        ///     Writes a single unsigned integer value to the server, taking care of Little-to Big-Endian conversion.
        /// </summary>
        /// <param name="value">The UInt32 value to be written.</param>
        public void WriteUInt32(uint value)
        {
            writer.Write(value);
        }

        /// <summary>
        ///     Writes a single Byte value to the server.
        /// </summary>
        /// <param name="value">The UInt32 value to be written.</param>
        public void WriteByte(byte value)
        {
            writer.Write(value);
        }

        /// <summary>
        ///     Writes a byte array to the server.
        /// </summary>
        /// <param name="buffer">The byte array to be written.</param>
        public void Write(byte[] buffer)
        {
            writer.Write(buffer);
        }

        /// <summary>
        ///     Reads the specified number of bytes of padding (i.e., garbage bytes) from the server.
        /// </summary>
        /// <param name="length">The number of bytes of padding to read.</param>
        public void ReadPadding(int length)
        {
            ReadBytes(length);
        }

        /// <summary>
        ///     Writes the specified number of bytes of padding (i.e., garbage bytes) to the server.
        /// </summary>
        /// <param name="length">The number of bytes of padding to write.</param>
        public void WritePadding(int length)
        {
            var padding = new byte[length];
            writer.Write(padding, 0, padding.Length);
        }

        /// <summary>
        ///     Converts a string to bytes for transfer to the server.
        /// </summary>
        /// <param name="text">The text to be converted to bytes.</param>
        /// <returns>Returns a Byte Array containing the text as bytes.</returns>
        protected static byte[] GetBytes(string text)
        {
            return System.Text.Encoding.ASCII.GetBytes(text);
        }

        /// <summary>
        ///     Converts a series of bytes to a string.
        /// </summary>
        /// <param name="bytes">The Array of Bytes to be converted to a string.</param>
        /// <returns>Returns a String representation of bytes.</returns>
        protected static string GetString(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }
}