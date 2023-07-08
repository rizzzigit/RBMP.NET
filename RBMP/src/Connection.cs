using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace RizzziGit.RBMP;

using Collections;

public delegate void ConnectionDisconnectHandler(Connection connection, Exception? exception);

public class Connection : IDisposable
{
  private static ConnectionConfig ValidateConfigAndClone(Connection connection, ConnectionConfig config)
  {
    List<Exception> exceptions = new();

    if (config.ReceiveBufferSizeLimit < 16)
    {
      exceptions.Add(new InvalidConfigException(connection, config, "ReceiveBufferSizeLimit", config.ReceiveBufferSizeLimit));
    }

    if (config.RequestTimeout < 0)
    {
      exceptions.Add(new InvalidConfigException(connection, config, "RequestTimeout", config.RequestTimeout));
    }

    if (config.ConcurrentPendingRequestLimit < 1)
    {
      exceptions.Add(new InvalidConfigException(connection, config, "ConcurrentPendingRequestLimit", config.ConcurrentPendingRequestLimit));
    }

    if (exceptions.Count != 0)
    {
      throw new AggregateException(exceptions);
    }

    return config.Clone();
  }

  public Connection(ConnectionConfig config, HttpListenerWebSocketContext listenerWebSocketContext) : this(config, listenerWebSocketContext.WebSocket) { }
  public Connection(ConnectionConfig config, WebSocket webSocket) : this(config, new WebSocketStreamBridge(webSocket)) { }
  public Connection(ConnectionConfig config, Stream stream)
  {
    if (!(stream.CanWrite && stream.CanRead))
    {
      throw new InvalidStreamException(this);
    }

    Stream = stream;
    Config = config.Clone();

    MessageQueue = new();
    RequestQueue = new();
    RemoteRequestCancellationQueue = new();
    PendingRequestQueue = new();

    SendMutex = new();

    Init();
  }

  private Stream Stream;

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
              throw new InvalidStreamException(this);
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
              throw new InvalidStreamException(this);
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
        }
        catch (Exception _exception)
        {
          Exception = _exception;
        }
        finally
        {
          ReceiveThread = null;
        }

        OnReceiveThreadStop(Exception);
      })).Start();

      Initialized = initResult;
    }

    return Initialized;
  }

  private Exception? Exception;

  public virtual bool IsConnected => ReceiveThread?.IsAlive == true;
  protected virtual int OnReceive(byte[] buffer, int offset, int count) => Stream.Read(buffer, offset, count);
  protected virtual int OnSend(byte[] buffer, int offset, int count)
  {
    Stream.Write(buffer, offset, count);
    return count;
  }

  public event ConnectionDisconnectHandler? Disconnected;

  public void Disconnect() => Disconnect(null);
  public void Disconnect(Exception? exception)
  {
    try
    {
      SendMutex.WaitOne();

      try
      {
        byte flag = 0b110000;
        if (exception != null)
        {
          flag |= 0b001000;
        }

        OnSend(new byte[] { 1, 0, 0, 0, flag }, 0, 5);
      }
      finally
      {
        SendMutex.ReleaseMutex();
      }
    }
    catch { }

    try { Stream.Close(); } catch { }
  }

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
        if ((flag & 0b010000) != 0)
        {
          return;
        }
        else
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
            if ((flag & 0b000100) != 0)
            {
              value.SetCanceled();
            }
            else
            {
              value.SetResult(new(this, id, (flag & 0b001000) != 0, payload));
            }
          }
        }
        else if ((flag & 0b001000) != 0)
        {
          if (RemoteRequestCancellationQueue.Remove(id, out CancellationTokenSource? value))
          {
            try { value.Cancel(); } catch { }
            SendResponseCancellation(id);
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

            CancellationTokenSource source = new();
            RemoteRequestCancellationQueue.TryAdd(id, source);
            RequestQueue.Enqueue(new(this, id, command, payload, source.Token));
          }
        }
      }
    }
  }

  private void OnReceiveThreadStop(Exception? exception)
  {
    Exception toBeThrown = new ConnectionClosedException(this, exception);

    try { MessageQueue.Dispose(toBeThrown); } catch { }
    try { RequestQueue.Dispose(toBeThrown); } catch { }
    try { SendMutex.Dispose(); } catch { }

    foreach (uint key in PendingRequestQueue.Keys)
    {
      if (PendingRequestQueue.Remove(key, out TaskCompletionSource<ConnectionResponseData>? value))
      {
        value.SetException(toBeThrown);
      }
    }

    Disconnected?.Invoke(this, exception);
  }

  private int ReceiveNextSegment = 0;
  private int Receive(byte[] buffer, int offset, int count, out int nextSegment)
  {
    if (RemoteConfig == null)
    {
      throw new InvalidOperationException(this, $"Remote is not yet ready to send messages.");
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
      throw new InvalidDataException(this, $"Message size {ReceiveNextSegment} received is larger than allowed {Config.ReceiveBufferSizeLimit}.");
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
  private WaitQueue<byte[]> MessageQueue;

  public byte[] ReceiveMessage() => MessageQueue.Dequeue();

  public int MaxSendMessageSize => (RemoteConfig?.ReceiveBufferSizeLimit ?? 0) - 1;
  public void SendMessage(byte[] buffer, int offset, int length)
  {
    if (!IsConnected)
    {
      throw new ConnectionClosedException(this, Exception);
    }

    int totalLength = length + 1;
    if (
      (totalLength > MaxSendMessageSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException(this, $"Message size {totalLength} is larger than allowed {MaxSendMessageSize}");
    }

    SendMutex.WaitOne();
    try
    {
      OnSend(BitConverter.GetBytes(length + 1), 0, 4);
      OnSend(new byte[]{ 0b100000 }, 0, 1);
      OnSend(buffer, offset, length);
    }
    catch (Exception exception)
    {
      Disconnect(exception);
      throw new ConnectionClosedException(this, exception);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }

  private WaitQueue<ConnectionRequestData> RequestQueue;
  private ConcurrentDictionary<uint, CancellationTokenSource> RemoteRequestCancellationQueue;
  private ConcurrentDictionary<uint, TaskCompletionSource<ConnectionResponseData>> PendingRequestQueue;

  public ConnectionRequestData ReceiveRequest() => RequestQueue.Dequeue();

  public int MaxSendRequestSize => (RemoteConfig?.ReceiveBufferSizeLimit ?? 0) - 9;
  public Task<ConnectionResponseData> SendRequestAsync(uint command, byte[] payload, int payloadOffset, int payloadLength, CancellationToken cancellationToken)
  {
    if (!IsConnected)
    {
      throw new ConnectionClosedException(this, Exception);
    }

    int totalLength = payloadLength + 9;
    if (
      (totalLength > MaxSendMessageSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException(this, $"Request size {totalLength} is alrger than allowed {MaxSendRequestSize}.");
    } else if (PendingRequestQueue.Count >= (RemoteConfig?.ConcurrentPendingRequestLimit ?? 0))
    {
      throw new InvalidOperationException(this, $"Max number of concurrent pending requests has been reached ({RemoteConfig?.ConcurrentPendingRequestLimit ?? 0})");
    }

    TaskCompletionSource<ConnectionResponseData> source = new();

    if (!cancellationToken.IsCancellationRequested)
    {
      var cancellationRegistration = cancellationToken.Register(() => source.SetCanceled(cancellationToken));
      uint id;
      do
      {
        id = (uint)Random.Shared.Next();
      } while (PendingRequestQueue.ContainsKey(id));

      PendingRequestQueue.TryAdd(id, source);
      SendMutex.WaitOne();
      try
      {
        OnSend(BitConverter.GetBytes(9 + payloadLength), 0, 4);
        OnSend(new byte[] { 0b000000 }, 0, 1);
        OnSend(BitConverter.GetBytes(id), 0, 4);
        OnSend(BitConverter.GetBytes(command), 0, 4);
        OnSend(payload, payloadOffset, payloadLength);
      }
      catch (Exception exception)
      {
        Disconnect(exception);
        throw new ConnectionClosedException(this, exception);
      }
      finally
      {
        SendMutex.ReleaseMutex();
      }
      cancellationRegistration.Unregister();

      {
        CancellationTokenRegistration?[] remoteCancellationTokenRegistration = new CancellationTokenRegistration?[] { null };

        remoteCancellationTokenRegistration[0] = cancellationToken.Register(() =>
        {
          remoteCancellationTokenRegistration[0]?.Unregister();

          SendRequestCancellation(id);
        });
      }
    }
    else
    {
      source.SetCanceled(cancellationToken);
    }

    return source.Task;
  }

  public ConnectionResponseData SendRequest(uint command, byte[] payload, int payloadOffset, int payloadLength, CancellationToken cancellationToken)
  {
    Task<ConnectionResponseData> task = SendRequestAsync(command, payload, payloadOffset, payloadLength, cancellationToken);

    try {
      task.Wait();

      return task.Result;
    }
    catch (AggregateException exception) { throw exception.GetBaseException() ?? throw new TaskCanceledException(); }
  }

  internal void SendRequestCancellation(uint id)
  {
    if (!IsConnected)
    {
      throw new ConnectionClosedException(this, Exception);
    }

    SendMutex.WaitOne();
    try
    {
      OnSend(new byte[] { 5, 0, 0, 0, 0b001000 }, 0, 5);
      OnSend(BitConverter.GetBytes(id), 0, 4);
    }
    catch (Exception exception)
    {
      Disconnect(exception);
      throw new ConnectionClosedException(this, exception);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }

  internal void SendResponseCancellation(uint id)
  {
    if (!IsConnected)
    {
      throw new ConnectionClosedException(this, Exception);
    }

    SendMutex.WaitOne();
    try
    {
      OnSend(new byte[] { 5, 0, 0, 0, 0b010100 }, 0, 5);
      OnSend(BitConverter.GetBytes(id), 0, 4);
    }
    catch (Exception exception)
    {
      Disconnect(exception);
      throw new ConnectionClosedException(this, exception);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }

  public int MaxSendResponseSize => (RemoteConfig?.ReceiveBufferSizeLimit ?? 0) - 5;
  internal void SendResponse(uint id, byte[] payload, int payloadOffset, int payloadLength, bool isError)
  {
    if (!IsConnected)
    {
      throw new ConnectionClosedException(this, Exception);
    }

    int totalLength = payloadLength + 5;
    if (
      (totalLength > MaxSendResponseSize) ||
      (totalLength < 0)
    )
    {
      throw new InvalidOperationException(this, $"Response size {totalLength} is alrger than allowed {MaxSendResponseSize}.");
    }

    {
      RemoteRequestCancellationQueue.Remove(id, out CancellationTokenSource? value);
    }

    SendMutex.WaitOne();
    try
    {
      OnSend(BitConverter.GetBytes(5 + payloadLength), 0, 4);
      OnSend(new byte[] { isError ? (byte)0b011000 : (byte)0b010000 }, 0, 1);
      OnSend(BitConverter.GetBytes(id), 0, 4);
      OnSend(payload, payloadOffset, payloadLength);
    }
    catch (Exception exception)
    {
      Disconnect(exception);
      throw new ConnectionClosedException(this, exception);
    }
    finally
    {
      SendMutex.ReleaseMutex();
    }
  }
}
