namespace RizzziGit.RBMP;

public class ConnectionRequestData
{
  internal ConnectionRequestData(Connection connection, uint id, uint command, byte[] payload)
  {
    ID = id;
    Command = command;
    Payload = payload;

    Connection = connection;
  }

  private Connection Connection;
  public uint ID { get; private set; }
  public uint Command { get; private set; }
  public byte[] Payload { get; private set; }

  public void SendResponse(byte[] payload, int payloadOffset, int payloadLlength) => Connection.SendResponse(ID, payload, payloadOffset, payloadLlength, false);
  public void SendError(byte[] payload, int payloadOffset, int payloadLlength) => Connection.SendResponse(ID, payload, payloadOffset, payloadLlength, true);
}
