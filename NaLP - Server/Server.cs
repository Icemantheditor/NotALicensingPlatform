﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using static NaLP___Server.Encryption;

namespace NaLP___Server
{
    public static class Headers
    {
        public const byte HEADER_CALL = 0x01;
        public const byte HEADER_RETURN = 0x02;
        public const byte HEADER_HANDSHAKE = 0x03;
        public const byte HEADER_MOVE = 0x04;
    }

    public class Server
    {
        private RNGCryptoServiceProvider cRandom = new RNGCryptoServiceProvider(DateTime.Now.ToString());
        private List<SharedClass> Clients = new List<SharedClass>();
        private List<KeyValuePair<string, MethodInfo>> RemotingMethods = new List<KeyValuePair<string, MethodInfo>>();
        private Socket sSocket = null;

        #region Variables

        /// <summary>
        /// Port for the server to listen on, changing this variable while the server is running will have no effect.
        /// </summary>
        public int iPort { get; set; }

        /// <summary>
        /// The number of maximum pending connections in queue.
        /// </summary>
        public int iBacklog { get; set; }

        /// <summary>
        /// If true, debugging information will be output to the console.
        /// </summary>
        public bool bDebugLog { get; set; } = false;

        #endregion Variables

        /// <summary>
        /// Initializes the NLC Server. Credits to Killpot :^)
        /// </summary>
        /// <param name="port">Port for server to listen on.</param>
        public Server(int port = 1337, int maxBacklog = 5)
        {
            this.iPort = port;
            this.iBacklog = maxBacklog;

            sSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Start server and begin listening for connections.
        /// </summary>
        public void Start()
        {
            sSocket.Bind(new IPEndPoint(IPAddress.Any, iPort));
            sSocket.Listen(iBacklog);

            Log("Server started!", ConsoleColor.Green);

            sSocket.BeginAccept(AcceptCallback, null);

            foreach (MethodInfo mI in typeof(SharedClass).GetMethods())
            {
                object[] oAttr = mI.GetCustomAttributes(typeof(NLCCall), false);

                if (oAttr.Any())
                {
                    NLCCall thisAttr = oAttr[0] as NLCCall;

                    if (RemotingMethods.Where(x => x.Key == thisAttr.Identifier).Any())
                        throw new Exception("There are more than one function inside the SharedClass with the same Identifier!");

                    Log(String.Format("Identifier {0} MethodInfo link created...", thisAttr.Identifier), ConsoleColor.Green);
                    RemotingMethods.Add(new KeyValuePair<string, MethodInfo>(thisAttr.Identifier, mI));
                }
            }
        }

        /// <summary>
        /// Stop listening for connections and close server.
        /// </summary>
        public void Stop()
        {
            if (sSocket == null)
                throw new Exception("Server is not running...");
            sSocket.Shutdown(SocketShutdown.Both);
            sSocket.Close();
        }

        private void AcceptCallback(IAsyncResult iAR)
        //Our method for accepting clients
        {
            Socket client = sSocket.EndAccept(iAR);
            Log(String.Format("Client connected from IP: {0}", client.RemoteEndPoint.ToString()), ConsoleColor.Green, true);

            sClient sC = new sClient();
            sC.cSocket = client;
            sC.iIndex = Clients.Count();

            Clients.Add(new SharedClass());

            sC.eCls = null;
            sC.bSize = new byte[4];

            BeginEncrypting(ref sC);

            sC.cSocket.BeginReceive(sC.bSize, 0, sC.bSize.Length, SocketFlags.None, RetrieveCallback, sC);

            sSocket.BeginAccept(AcceptCallback, null);
        }

        private void RetrieveCallback(IAsyncResult iAR)
        // Handshake + Encryption is handled outside of this callback, so any message that makes it here is expected to be a method call/move.
        {
            sClient sC = (sClient)iAR.AsyncState;
            try
            {
                SocketError sE;
                if (sC.cSocket.EndReceive(iAR, out sE) == 0 || sE != SocketError.Success)
                {
                    Log(String.Format("Client IP: {0} has disconnected...", sC.cSocket.RemoteEndPoint.ToString()), ConsoleColor.Yellow, true);
                    sC.cSocket.Close();
                    Clients[sC.iIndex].Dispose();
                    Clients.RemoveAt(sC.iIndex);
                    // GC.Collect(0); For immediate disposal
                    return;
                }

                byte[] cBuffer = new byte[BitConverter.ToInt32(sC.bSize, 0)];
                sC.bSize = new byte[4];

                sC.cSocket.Receive(cBuffer);

                Log(String.Format("Receiving {0} bytes...", cBuffer.Length), ConsoleColor.Cyan);

                if (cBuffer.Length <= 0)
                    throw new Exception("Received null buffer from client!");

                cBuffer = sC.eCls.AES_Decrypt(cBuffer);

                object[] oMsg = BinaryFormatterSerializer.Deserialize(cBuffer);

                if (!oMsg[0].Equals(Headers.HEADER_CALL) && !oMsg[0].Equals(Headers.HEADER_MOVE)) // Check to make sure it's a method call/move
                    throw new Exception("Ahhh it's not a call or move, everyone run!");

                object[] oRet = new object[2];
                oRet[0] = Headers.HEADER_RETURN;

                MethodInfo[] mIA = typeof(SharedClass).GetMethods();

                MethodInfo mI = RemotingMethods.Find(x => x.Key == oMsg[1] as string).Value;

                if (mI == null)
                    throw new Exception("Client called method that does not exist in Shared Class! (Did you remember the [NLCCall] Attribute?)");

                oRet[1] = mI.Invoke(Clients[sC.iIndex], oMsg.Skip(2).Take(oMsg.Length - 2).ToArray());

                Log(String.Format("Client IP: {0} called Remote Identifier: {1}", sC.cSocket.RemoteEndPoint.ToString(), oMsg[1] as string), ConsoleColor.Cyan);

                if (oRet[1] == null && oMsg[0].Equals(Headers.HEADER_MOVE))
                    Console.WriteLine("Method {0} returned null! Possible mismatch?", oMsg[1] as string);

                BlockingSend(sC, oRet);
                sC.cSocket.BeginReceive(sC.bSize, 0, sC.bSize.Length, SocketFlags.None, RetrieveCallback, sC);
            }
            catch (Exception ex)
            {
                Log(String.Format("Client IP: {0} has caused an exception...", sC.cSocket.RemoteEndPoint.ToString()), ConsoleColor.Yellow, true);
                Log(ex.Message + Environment.NewLine + ex.StackTrace, ConsoleColor.Red);
                sC.cSocket.Close();
                Clients[sC.iIndex].Dispose();
                Clients.RemoveAt(sC.iIndex);
            }
        }

        private void BeginEncrypting(ref sClient sC)
        {
            byte[] sSymKey;
            CngKey sCngKey = CngKey.Create(CngAlgorithm.ECDiffieHellmanP521);
            byte[] sPublic = sCngKey.Export(CngKeyBlobFormat.EccPublicBlob);

            BlockingSend(sC, Headers.HEADER_HANDSHAKE, sPublic);

            object[] oRecv = BlockingReceive(sC);

            if (!oRecv[0].Equals(Headers.HEADER_HANDSHAKE))
                sC.cSocket.Disconnect(true);

            byte[] cBuf = oRecv[1] as byte[];

            using (var sAlgo = new ECDiffieHellmanCng(sCngKey))
            using (CngKey cPubKey = CngKey.Import(cBuf, CngKeyBlobFormat.EccPublicBlob))
                sSymKey = sAlgo.DeriveKeyMaterial(cPubKey);

            sC.eCls = new Encryption(sSymKey);
        }

        private void BlockingSend(sClient sC, params object[] param)
        {
            byte[] bSend = BinaryFormatterSerializer.Serialize(param);
            if (sC.eCls != null)
                bSend = sC.eCls.AES_Encrypt(bSend);
            else
                bSend = Compress(bSend);

            Log(String.Format("Sending {0} bytes...", bSend.Length), ConsoleColor.Cyan);

            sC.cSocket.Send(BitConverter.GetBytes(bSend.Length));
            sC.cSocket.Send(bSend);
        }

        private object[] BlockingReceive(sClient sC)
        {
            byte[] bSize = new byte[4];
            sC.cSocket.Receive(bSize);

            byte[] sBuf = new byte[BitConverter.ToInt32(bSize, 0)];
            int iReceived = 0;

            while (iReceived < sBuf.Length)
            {
                iReceived += sC.cSocket.Receive(sBuf, iReceived, sBuf.Length - iReceived, SocketFlags.None);
            }

            Log(String.Format("Receiving {0} bytes...", sBuf.Length), ConsoleColor.Cyan);

            if (sC.eCls != null)
                sBuf = sC.eCls.AES_Decrypt(sBuf);
            else
                sBuf = Encryption.Decompress(sBuf);

            return BinaryFormatterSerializer.Deserialize(sBuf);
        }

        private void Log(string message, ConsoleColor color = ConsoleColor.Gray, bool force = false)
        {
            if (!bDebugLog && !force)
                return;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("[{0}] ", DateTime.Now.ToLongTimeString());
            Console.ForegroundColor = color;
            Console.Write("{0}{1}", message, Environment.NewLine);
            Console.ResetColor();
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class NLCCall : Attribute
    {
        public readonly string Identifier;

        public NLCCall(string Identifier)
        {
            this.Identifier = Identifier;
        }
    }

    public class sClient
    {
        public Socket cSocket;
        public int iIndex;
        public Encryption eCls;
        public byte[] bSize;
    }
}
