namespace RizzziGit.RBMP;

public class ConnectionConfig
{
  public static readonly int ConfigBufferSize = 24;

  public static ConnectionConfig Deserialize(byte[] buffer)
  {
    if (buffer.Length != ConfigBufferSize)
    {
      throw new System.InvalidOperationException($"Invalid config data.");
    }

    ulong clientId = BitConverter.ToUInt64(buffer, 0);
    int receiveBufferSizeLimit = BitConverter.ToInt32(buffer, 8);
    if (receiveBufferSizeLimit < 0)
    {
      throw new FormatException();
    }

    int requestTimeout = BitConverter.ToInt32(buffer, 12);
    if (requestTimeout < 0)
    {
      throw new FormatException();
    }

    int concurrentPendingRequestLimit = BitConverter.ToInt32(buffer, 16);
    if (concurrentPendingRequestLimit < 0)
    {
      throw new FormatException();
    }

    return new(clientId)
    {
      ReceiveBufferSizeLimit = receiveBufferSizeLimit,
      RequestTimeout = requestTimeout,
      ConcurrentPendingRequestLimit = concurrentPendingRequestLimit
    };
  }

  public ConnectionConfig(ulong clientId)
  {
    ClientID = clientId;
  }

  public ulong ClientID;
  public int ReceiveBufferSizeLimit = 1024 * 1024;
  public int RequestTimeout = 5000;
  public int ConcurrentPendingRequestLimit = 16;

  public ConnectionConfig Clone()
  {
    return new ConnectionConfig(ClientID)
    {
      ReceiveBufferSizeLimit = ReceiveBufferSizeLimit,
      RequestTimeout = RequestTimeout,
      ConcurrentPendingRequestLimit = ConcurrentPendingRequestLimit
    };
  }

  public byte[] Serialize()
  {
    byte[] buffer = new byte[ConfigBufferSize];

    Serialize(buffer, 0);
    return buffer;
  }

  public void Serialize(byte[] destination, int offset)
  {
    Array.Copy(BitConverter.GetBytes(ClientID), 0, destination, offset + 0, 8);
    Array.Copy(BitConverter.GetBytes(ReceiveBufferSizeLimit), 0, destination, offset + 8, 4);
    Array.Copy(BitConverter.GetBytes(RequestTimeout), 0, destination, offset + 12, 4);
    Array.Copy(BitConverter.GetBytes(ConcurrentPendingRequestLimit), 0, destination, offset + 16, 4);
  }
}
