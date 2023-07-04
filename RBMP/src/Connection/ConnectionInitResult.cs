namespace RizzziGit.RBMP;

internal class ConnectionInitResult
{
  internal ConnectionInitResult(ConnectionConfig remoteConfig)
  {
    RemoteConfig = remoteConfig;
  }

  public ConnectionConfig RemoteConfig { get; private set; }
}
