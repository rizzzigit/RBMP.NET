namespace RizzziGit.RBMP;

public class ConnectionResponseData
{
  internal ConnectionResponseData(Connection connection, uint id, bool isError, byte[] payload)
  {
    Connection = connection;
    ID = id;
    IsError = isError;
    Payload = payload;
  }

  public Connection Connection { get; private set; }
  public uint ID { get; private set; }
  public bool IsError { get; private set; }
  public byte[] Payload { get; private set; }
}
