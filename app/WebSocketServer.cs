using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LightRecorder {

  /// <summary>
  /// A small RFC 6455 server, bound to loopback.
  ///
  /// The Electron build used the `ws` npm package, which meant Node, which
  /// meant a package manager and a node_modules folder measured in hundreds of
  /// megabytes. The protocol the extension actually needs is a handshake and
  /// four frame opcodes.
  ///
  /// HttpListener would have been shorter still, but binding a prefix with it
  /// needs an administrator URL reservation, and asking the user to run an
  /// elevated netsh command to record their screen is not a trade worth making.
  /// A raw TcpListener on 127.0.0.1 needs no privileges at all.
  /// </summary>
  internal class WebSocketServer {

    const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>A single frame larger than this is refused rather than
    /// allocated. Tab-capture chunks run to a few hundred KB.</summary>
    const int MaxFrameBytes = 32 * 1024 * 1024;

    TcpListener _listener;
    Thread _acceptThread;
    volatile bool _running;

    readonly List<WebSocketConnection> _clients = new List<WebSocketConnection>();
    readonly object _gate = new object();

    public event Action<WebSocketConnection> Opened;
    public event Action<WebSocketConnection> Closed;
    public event Action<WebSocketConnection, string> TextMessage;
    public event Action<WebSocketConnection, byte[], int> BinaryMessage;
    public event Action<string> Error;

    public int Port { get; private set; }
    public bool IsListening { get { return _running; } }

    // ------------------------------------------------------------ lifecycle

    public bool Start(int port) {
      Stop();
      Port = port;
      try {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        // ffmpeg is started with redirected stdio, which hands it every
        // inheritable handle we hold. Without this it inherits this socket and
        // can keep the port bound long after we are gone.
        Native.DontInherit(_listener.Server.Handle);
      } catch (SocketException err) {
        Raise(err.SocketErrorCode == SocketError.AddressAlreadyInUse
          ? "Port " + port + " is already in use. Pick another in Settings."
          : err.Message);
        _listener = null;
        return false;
      } catch (Exception err) {
        Raise(err.Message);
        _listener = null;
        return false;
      }

      _running = true;
      _acceptThread = new Thread(AcceptLoop);
      _acceptThread.IsBackground = true;
      _acceptThread.Name = "bridge-accept";
      _acceptThread.Start();
      return true;
    }

    public void Stop() {
      _running = false;

      TcpListener l = _listener;
      _listener = null;
      if (l != null) { try { l.Stop(); } catch (Exception) { } }

      List<WebSocketConnection> snapshot;
      lock (_gate) { snapshot = new List<WebSocketConnection>(_clients); _clients.Clear(); }
      foreach (WebSocketConnection c in snapshot) c.Close();

      Thread t = _acceptThread;
      _acceptThread = null;
      if (t != null) { try { t.Join(500); } catch (Exception) { } }
    }

    void AcceptLoop() {
      while (_running) {
        TcpClient client = null;
        try {
          client = _listener.AcceptTcpClient();
        } catch (Exception) {
          if (_running) Thread.Sleep(100);
          continue;
        }

        // Belt and braces: the listener is already bound to loopback, but a
        // second check costs nothing and documents the intent.
        try {
          var remote = client.Client.RemoteEndPoint as IPEndPoint;
          if (remote == null || !IPAddress.IsLoopback(remote.Address)) { client.Close(); continue; }
        } catch (Exception) {
          try { client.Close(); } catch (Exception) { }
          continue;
        }

        var conn = new WebSocketConnection(this, client);
        lock (_gate) _clients.Add(conn);
        conn.Start();
      }
    }

    internal void Remove(WebSocketConnection conn) {
      lock (_gate) _clients.Remove(conn);
      Action<WebSocketConnection> h = Closed;
      if (h != null) h(conn);
    }

    internal void RaiseOpened(WebSocketConnection c) {
      Action<WebSocketConnection> h = Opened;
      if (h != null) h(c);
    }

    internal void RaiseText(WebSocketConnection c, string text) {
      Action<WebSocketConnection, string> h = TextMessage;
      if (h != null) h(c, text);
    }

    internal void RaiseBinary(WebSocketConnection c, byte[] data, int count) {
      Action<WebSocketConnection, byte[], int> h = BinaryMessage;
      if (h != null) h(c, data, count);
    }

    internal void Raise(string message) {
      Action<string> h = Error;
      if (h != null) h(message);
      Log.Warn("bridge: " + message);
    }

    internal static string AcceptKey(string clientKey) {
      using (var sha = new SHA1Managed()) {
        byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(clientKey + Magic));
        return Convert.ToBase64String(hash);
      }
    }

    internal static int MaxFrame { get { return MaxFrameBytes; } }
  }

  // ==========================================================================

  /// <summary>One connected socket. The extension opens two: the service
  /// worker for control messages, and the offscreen document for video
  /// chunks, so encoded data never passes through the worker.</summary>
  internal class WebSocketConnection {

    readonly WebSocketServer _server;
    readonly TcpClient _client;
    NetworkStream _stream;
    Thread _thread;
    volatile bool _open;
    readonly object _writeGate = new object();

    /// <summary>Set by the bridge once the pairing token has been checked.</summary>
    public bool Authenticated;

    /// <summary>"control" or "data", from the extension's hello.</summary>
    public string Role = "control";

    public bool IsOpen { get { return _open; } }

    internal WebSocketConnection(WebSocketServer server, TcpClient client) {
      _server = server;
      _client = client;
    }

    internal void Start() {
      _thread = new Thread(Run);
      _thread.IsBackground = true;
      _thread.Name = "bridge-conn";
      _thread.Start();
    }

    void Run() {
      try {
        _client.NoDelay = true;
        _stream = _client.GetStream();

        if (!Handshake()) { Close(); return; }
        _open = true;
        _server.RaiseOpened(this);
        ReadLoop();
      } catch (Exception) {
        // A browser closing a tab looks exactly like this. Not worth logging.
      } finally {
        _open = false;
        try { if (_stream != null) _stream.Dispose(); } catch (Exception) { }
        try { _client.Close(); } catch (Exception) { }
        _server.Remove(this);
      }
    }

    // ------------------------------------------------------------ handshake

    bool Handshake() {
      _client.ReceiveTimeout = 10000;
      string request = ReadHeaders();
      _client.ReceiveTimeout = 0;
      if (request == null) return false;

      string key = null;
      bool upgrade = false;
      foreach (string raw in request.Split('\n')) {
        string line = raw.Trim();
        if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
          key = line.Substring(18).Trim();
        else if (line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase) &&
                 line.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0)
          upgrade = true;
      }
      if (!upgrade || string.IsNullOrEmpty(key)) return false;

      string response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "Sec-WebSocket-Accept: " + WebSocketServer.AcceptKey(key) + "\r\n\r\n";

      byte[] bytes = Encoding.ASCII.GetBytes(response);
      _stream.Write(bytes, 0, bytes.Length);
      _stream.Flush();
      return true;
    }

    string ReadHeaders() {
      var sb = new StringBuilder();
      var one = new byte[1];
      int consecutive = 0;

      while (sb.Length < 16384) {
        int n;
        try { n = _stream.Read(one, 0, 1); } catch (Exception) { return null; }
        if (n <= 0) return null;

        char c = (char)one[0];
        sb.Append(c);
        if (c == '\n') { consecutive++; if (consecutive == 2) return sb.ToString(); }
        else if (c != '\r') consecutive = 0;
      }
      return null;
    }

    // ------------------------------------------------------------- reading

    void ReadLoop() {
      var header = new byte[14];
      byte[] fragment = null;
      int fragmentLength = 0;
      int fragmentOpcode = 0;

      while (_open) {
        if (!ReadExact(header, 0, 2)) return;

        bool fin = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        bool masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126) {
          if (!ReadExact(header, 0, 2)) return;
          length = (header[0] << 8) | header[1];
        } else if (length == 127) {
          if (!ReadExact(header, 0, 8)) return;
          length = 0;
          for (int i = 0; i < 8; i++) length = (length << 8) | header[i];
        }

        if (length < 0 || length > WebSocketServer.MaxFrame) {
          _server.Raise("frame of " + length + " bytes refused");
          return;
        }

        var mask = new byte[4];
        if (masked && !ReadExact(mask, 0, 4)) return;

        var payload = new byte[length];
        if (length > 0 && !ReadExact(payload, 0, (int)length)) return;

        if (masked) {
          for (int i = 0; i < length; i++) payload[i] ^= mask[i & 3];
        }

        switch (opcode) {
          case 0x0:     // continuation
            if (fragment == null) return;
            fragment = Append(fragment, ref fragmentLength, payload);
            if (fin) { Deliver(fragmentOpcode, fragment, fragmentLength); fragment = null; fragmentLength = 0; }
            break;

          case 0x1:     // text
          case 0x2:     // binary
            if (fin) {
              Deliver(opcode, payload, (int)length);
            } else {
              fragmentOpcode = opcode;
              fragment = payload;
              fragmentLength = (int)length;
            }
            break;

          case 0x8:     // close
            SendFrame(0x8, new byte[0], 0);
            return;

          case 0x9:     // ping
            SendFrame(0xA, payload, (int)length);
            break;

          case 0xA:     // pong
            break;
        }
      }
    }

    static byte[] Append(byte[] buffer, ref int used, byte[] extra) {
      if (used + extra.Length > buffer.Length) {
        var bigger = new byte[Math.Max(buffer.Length * 2, used + extra.Length)];
        Buffer.BlockCopy(buffer, 0, bigger, 0, used);
        buffer = bigger;
      }
      Buffer.BlockCopy(extra, 0, buffer, used, extra.Length);
      used += extra.Length;
      return buffer;
    }

    void Deliver(int opcode, byte[] payload, int count) {
      if (opcode == 0x1) _server.RaiseText(this, Encoding.UTF8.GetString(payload, 0, count));
      else if (opcode == 0x2) _server.RaiseBinary(this, payload, count);
    }

    bool ReadExact(byte[] buffer, int offset, int count) {
      int got = 0;
      while (got < count) {
        int n;
        try { n = _stream.Read(buffer, offset + got, count - got); }
        catch (Exception) { return false; }
        if (n <= 0) return false;
        got += n;
      }
      return true;
    }

    // ------------------------------------------------------------- writing

    public bool SendText(string text) {
      byte[] bytes = Encoding.UTF8.GetBytes(text);
      return SendFrame(0x1, bytes, bytes.Length);
    }

    bool SendFrame(int opcode, byte[] payload, int count) {
      if (!_open && opcode != 0x8) return false;

      // Server-to-client frames are never masked.
      var header = new byte[10];
      int headerLength;
      header[0] = (byte)(0x80 | opcode);

      if (count < 126) {
        header[1] = (byte)count;
        headerLength = 2;
      } else if (count <= ushort.MaxValue) {
        header[1] = 126;
        header[2] = (byte)(count >> 8);
        header[3] = (byte)count;
        headerLength = 4;
      } else {
        header[1] = 127;
        for (int i = 0; i < 8; i++) header[2 + i] = (byte)(((long)count >> ((7 - i) * 8)) & 0xFF);
        headerLength = 10;
      }

      try {
        lock (_writeGate) {
          _stream.Write(header, 0, headerLength);
          if (count > 0) _stream.Write(payload, 0, count);
          _stream.Flush();
        }
        return true;
      } catch (Exception) {
        _open = false;
        return false;
      }
    }

    public void Close() {
      if (_open) { try { SendFrame(0x8, new byte[0], 0); } catch (Exception) { } }
      _open = false;
      try { if (_stream != null) _stream.Close(); } catch (Exception) { }
      try { _client.Close(); } catch (Exception) { }
    }
  }
}
