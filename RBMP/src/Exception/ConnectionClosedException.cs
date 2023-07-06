namespace RizzziGit.RBMP;

public class ConnectionException : Exception
{
  public ConnectionException(Connection connection, string message) : base(message)
  {
    Connection = connection;
  }

  public readonly Connection Connection;
}

public class ConnectionClosedException : ConnectionException
{
  public ConnectionClosedException(Connection connection, bool graceful) : base(connection, "Connection is closed.")
  {
    WasGraceful = graceful;
  }

  public readonly bool WasGraceful;
}

public class InvalidStreamException : ConnectionException
{
  public InvalidStreamException(Connection connection) : base(connection, "Stream must be duplex (both can read and write) and must not be closed.")
  {

  }
}

public class InvalidOperationException : ConnectionException
{
  public InvalidOperationException(Connection connection, string message) : base(connection, message)
  {

  }
}

public class InvalidDataException : ConnectionException
{
  public InvalidDataException(Connection connection, string message) : base(connection, message)
  {

  }
}
