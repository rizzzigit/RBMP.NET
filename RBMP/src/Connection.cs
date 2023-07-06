using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace RizzziGit.RBMP;

using Collections;

public delegate void ConnectionDisconnectHandler(Connection connection, Exception? exception);

public class Connection : IDisposable
{
  public Connection(ConnectionConfig config, WebSocket webSocket) : this(config, new WebSocketStreamBridge(webSocket)) { }
  public Connection(ConnectionConfig config, Stream underlyingStream)
  {
    if (!(underlyingStream.CanWrite && underlyingStream.CanRead))
    {
      throw new InvalidOperationException("Duplex stream required.");
    }

    UnderlyingStream = underlyingStream;
    Config = config.Clone();

    MessageQueue = new();
    RequestQueue = new();
    PendingRequestQueue = new();

    SendMutex = new();

    Init();
  }

  private Stream UnderlyingStream;

  public void Dispose() => Disconnect();

  private ConnectionConfig Config;
  private ConnectionConfig? RemoteConfig;

  public ulong ClientID => Config.ClientID;
  public ulong RemoteClientId => RemoteConfig?.ClientID ?? 0;

  private ConnectionInitResult? Initialized;
  private Thread? ReceiveThread;
  private ConnectionInitResult Init()
  {
    if (Initialized == null)
    {
      // Send Local Config
      {
        byte[] configBuffer = new byte[ConnectionConfig.ConfigBufferSize];
        Config.Serialize(configBuffer, 0);

        OnSend(BitConverter.GetBytes(configBuffer.Length), 0, 4);
        OnSend(configBuffer, 0, configBuffer.Length);
      }

      // Receive Remote Config
      ConnectionInitResult initResult;
      {
        int configBufferSize;
        {
          byte[] configBufferLengthBuffer = new byte[4];
          int received = 0;

          do
          {
            int result = OnReceive(configBufferLengthBuffer, received, configBufferLengthBuffer.Length - received);
            if (result == 0)
            {
              throw new InvalidDataException("Already end of stream.");
            }

            received += result;
          }
          while (received < 4);

          configBufferSize = BitConverter.ToInt32(configBufferLengthBuffer);
        }

        byte[] configBuffer = new byte[configBufferSize];
        {
          int received = 0;

          do
          {
            int result = OnReceive(configBuffer, received, configBuffer.Length - received);
            if (result == 0)
            {
              throw new InvalidDataException("Already end of stream.");
            }

            received += result;
          } while (received < configBuffer.Length);
        }

        initResult = new(
          remoteConfig: RemoteConfig = ConnectionConfig.Deserialize(configBuffer)
        );
      }

      (ReceiveThread = new(() =>
      {
        try
        {
          RunReceiveThread(initResult);
          OnReceiveThreadStop(null);
        }
        catch (Exception exception)
        {
          Disconnect(ReceiveThreadException = exception);
          OnReceiveThreadStop(exception);
        }
        finally
        {
          SendMutex.Dispose();
          ReceiveThread = null;
        }
      })).Start();

      return (Initialized = initResult);
    }

    return Initialized;
  }

  public virtual bool IsConnected => ReceiveThread?.IsAlive == true;
  protected virtual int OnReceive(byte[] buffer, int offset, int count) => UnderlyingStream.Read(buffer, offset, count);
  protected virtual int OnSend(byte[] buffer, int offset, int count)
  {
    UnderlyingStream.Write(buffer, offset, count);
    return count;
  }
  protected virtual void OnDisconnect(Exception? exception)
  {
    try
    {
      UnderlyingStream.Close();
    }
    finally
    {
      Disconnected?.Invoke(this, exception);
    }
  }

  public event ConnectionDisconnectHandler? Disconnected;

  public void Disconnect() => Disconnect(null);
  public void Disconnect(Exception? exception) => OnDisconnect(exception);

  private Exception? ReceiveThreadException;
  private void RunReceiveThread(ConnectionInitResult result) => RunReceiveThread(result.RemoteConfig);
  private void RunReceiveThread(ConnectionConfig remoteConfig)
  {
    while (IsConnected)
    {
      byte flag;
      {
        byte[] flagBuffer = new byte[1];
        {
          int result = Receive(flagBuffer, 0, 1, out int nextSegment);
          if (result == 0)
          {
            break;
          }
        }

        flag = flagBuffer[0];
      }

      if ((flag & 0b100000) != 0)
      {
        byte[] buffer;
        {
          Receive(new byte[0], 0, 0, out int totalLength);
          buffer = new byte[totalLength];
        }

        int bufferLength = 0;
        do
        {
          int result = Receive(buffer, bufferLength, Int32.Min(Config.ReceiveBufferSizeLimit, buffer.Length), out int nextSegment);
          if (result == 0)
          {
            break;
          }

          bufferLength += result;
        }
        while (bufferLength < buffer.Length);

        MessageQueue.Enqueue(buffer);
      }
      else
      {
        int totalLength;
        {
          Receive(new byte[0], 0, 0, out int nextSegment);
          totalLength = nextSegment;
        }

        uint id;
        {
          byte[] idBuffer = new byte[4];
          int idLength = 0;

          do
          {
            int result = Receive(idBuffer, idLength, idBuffer.Length - idLength, out int nextSegment);
            if (result == 0)
            {
              return;
            }

            idLength += result;
          }
          while (idLength < 4);

          id = BitConverter.ToUInt32(idBuffer);
        }

        if ((flag & 0b010000) != 0)
        {
          byte[] payload = new byte[totalLength - 4];
          {
            if (payload.Length != 0)
            {
              int payloadLength = 0;

              do
              {
                int result = Receive(payload, payloadLength, payload.Length - payloadLength, out int nextSegment);
                if (result == 0)
                {
                  return;
                }

                payloadLength += result;
              }
              while (payloadLength < payload.Length);
            }
          }

          if (PendingRequestQueue.Remove(id, out TaskCompletionSource<ConnectionResponseData>? value))
          {
            value.SetResult(new(this, id, (flag & 0b001000) != 0, payload));
          }
        }
        else
        {
          uint command;
          {
            byte[] commandBuffer = new byte[4];
            int commandLength = 0;

            do
            {
              int result = Receive(commandBuffer, commandLength, commandBuffer.Length - commandLength, out int nextSegment);
              if (result == 0)
              {
                return;
              }

              commandLength += result;
            }
            while (commandLength < 4);

            command = BitConverter.ToUInt32(commandBuffer);
          }

          byte[] payload = new byte[totalLength - 8];
          {
            if (payload.Length != 0)
            {
              int payloadLength = 0;

              do
              {
                int result = Receive(payload, payloadLength, payload.Length - payloadLength, out int nextSegment);
                if (result == 0)
                {
                  return;
                }

                payloadLength += result;
              }
              while (payloadLength < payload.Length);
            }

            RequestQueue.Enqueue(new(this, id, command, payload));
          }
        }
      }
    }
  }

  private void OnReceiveThreadStop(Exception? exception)
  {
    MessageQueue.Dispose(exception);
    RequestQueue.Dispose(exception);

    foreach (uint key in PendingRequestQueue.Keys)
    {
      if (PendingRequestQueue.Remove(key, out TaskCompletionSource<ConnectionResponseData>? value))
      {
        if (exception != null)
        {
          value.SetException(exception);
        }
        else
        {
          value.SetCanceled();
        }
      }
    }
  }

  private int ReceiveNextSegment = 0;
  private int Receive(byte[] buffer, int offset, int count, out int nextSegment)
  {
    if (RemoteConfig == null)
    {
      throw new InvalidOperationException($"Remote is not yet ready to send messages.");
    }

    if (ReceiveThread == null)
    {
      if (ReceiveThreadException != null)
      {
        throw ReceiveThreadException;
      }
      else
      {
        RunReceiveThread(RemoteConfig);
      }
    }

    if (ReceiveNextSegment <= 0)
    {
      byte[] lengthBuffer = new byte[4];
      int lengthBufferLength = 0;

      do
      {
        int received = OnReceive(lengthBuffer, lengthBufferLength, 4 - lengthBufferLength);
        if (received == 0)
        {
          return (nextSegment = 0);
        }

        lengthBufferLength += received;
      } while (lengthBufferLength < 4);

      ReceiveNextSegment = BitConverter.ToInt32(lengthBuffer);
    }

    if (Config.ReceiveBufferSizeLimit < ReceiveNextSegment)
    {
      Exception exception = new InvalidOperationException($"Message size {ReceiveNextSegment} received is larger than allowed {Config.ReceiveBufferSizeLimit}.");

      Disconnect(exception);
      throw exception;
    }

    if (count == 0)
    {
      return (nextSegment = ReceiveNextSegment);
    }
    else
    {
      int bufferLength = 0;

      do
      {
        int received = OnReceive(buffer, bufferLength, Int32.Min(buffer.Length - bufferLength, ReceiveNextSegment - bufferLength));
        if (received == 0)
        {
          return (nextSegment = 0);
        }

        ReceiveNextSegment -= received;
        bufferLength += received;
      } while (bufferLength < Int32.Min(ReceiveNextSegment, count));

      nextSegment = ReceiveNextSegment;
      return bufferLength;
    }
  }


  private Mutex SendMutex;

  private void CheckSendSize(int length)
  {
    if (RemoteConfig == null)
    {
      throw new InvalidOperationException($"Remote is not yet ready to receive messages.");
    }
    else if (length > RemoteConfig.ReceiveBufferSizeLimit)
    {
      throw new InvalidOperationException($"Attempt to send message {length} larger than allowed {RemoteConfig.ReceiveBufferSizeLimit}.");
    }
  }
  // private MessageCodec? IncomingDecryptor;
  // private MessageCodec? OutgoingEncryptor;

  private WaitQueue<byte[]> MessageQueue;

  public Task<byte[]> ReceiveMessageAsync() => MessageQueue.DequeueAsync();
  public byte[] ReceiveMessage() => MessageQueue.Dequeue();

  public int MaxSendMessageSize => (RemoteConfig?.ReceiveBufferSizeLimit ?? 0) - 1;
  public void SendMessage(byte[] buffer, int offset, int length)
  {
    int totalLength = length + 1;
    if (
      (totalLength > MaxSendMessageSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException($"Message size {totalLength} is larger than allowed {MaxSendMessageSize}");
    }

    CheckSendSize(length + 1);
    SendMutex.WaitOne();
    try
    {
      OnSend(BitConverter.GetBytes(length + 1), 0, 4);
      OnSend(new byte[]{ 0b100000 }, 0, 1);
      OnSend(buffer, offset, length);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }

  private WaitQueue<ConnectionRequestData> RequestQueue;
  private ConcurrentDictionary<uint, TaskCompletionSource<ConnectionResponseData>> PendingRequestQueue;

  public ConnectionRequestData ReceiveRequest() => RequestQueue.Dequeue();
  public Task<ConnectionRequestData> ReceiveRequestAsync() => RequestQueue.DequeueAsync();

  public int MaxSendRequestSize => (RemoteConfig?.ReceiveBufferSizeLimit ?? 0) - 9;
  public Task<ConnectionResponseData> SendRequestAsync(uint command, byte[] payload, int payloadOffset, int payloadLength)
  {
    int totalLength = payloadLength + 9;
    if (
      (totalLength > MaxSendMessageSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException($"Request size {totalLength} is alrger than allowed {MaxSendRequestSize}.");
    } else if (PendingRequestQueue.Count >= (RemoteConfig?.ConcurrentPendingRequestLimit ?? 0))
    {
      throw new InvalidOperationException($"Max number of concurrent pending requests is {RemoteConfig?.ConcurrentPendingRequestLimit ?? 0}");
    }

    TaskCompletionSource<ConnectionResponseData> source = new();
    uint id = (uint)Random.Shared.Next();

    SendMutex.WaitOne();
    try
    {
      OnSend(BitConverter.GetBytes(9 + payloadLength), 0, 4);
      OnSend(new byte[] { 0b000000 }, 0, 1);
      OnSend(BitConverter.GetBytes(id), 0, 4);
      OnSend(BitConverter.GetBytes(command), 0, 4);
      OnSend(payload, payloadOffset, payloadLength);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }

    PendingRequestQueue.TryAdd(id, source);
    return source.Task;
  }

  public ConnectionResponseData SendRequest(uint command, byte[] payload, int payloadOffset, int payloadLength)
  {
    Task<ConnectionResponseData> task = SendRequestAsync(command, payload, payloadOffset, payloadLength);

    try {
      task.Wait();

      return task.Result;
    }
    catch (AggregateException exception) { throw exception.GetBaseException() ?? throw new TaskCanceledException(); }
  }

  internal void SendResponse(uint id, byte[] payload, int payloadOffset, int payloadLength, bool isError)
  {
    int totalLength = payloadLength + 5;
    if (
      (totalLength > MaxSendMessageSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException($"Request size {totalLength} is alrger than allowed {MaxSendRequestSize}.");
    }

    SendMutex.WaitOne();
    try
    {
      OnSend(BitConverter.GetBytes(5 + payloadLength), 0, 4);
      OnSend(new byte[] { isError ? (byte)0b011000 : (byte)0b010000 }, 0, 1);
      OnSend(BitConverter.GetBytes(id), 0, 4);
      OnSend(payload, payloadOffset, payloadLength);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }
}
