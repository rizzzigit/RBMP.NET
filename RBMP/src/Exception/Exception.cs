namespace RizzziGit.RBMP;

public class ConnectionException : Exception
{
  public ConnectionException(Connection connection, string message) : this(connection, message, null) { }
  public ConnectionException(Connection connection, string message, Exception? cause) : base(message, cause)
  {
    Connection = connection;
  }

  public readonly Connection Connection;
}

public class ConnectionClosedException : ConnectionException
{
  public ConnectionClosedException(Connection connection, Exception? cause) : base(connection, "Connection is closed.")
  {
    Cause = cause;
  }

  public readonly Exception? Cause;
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

public class InvalidConfigException : ConnectionException
{
  public InvalidConfigException(Connection connection, ConnectionConfig config, string fieldName, object fieldValue) : base(connection, $"Invalid config value {fieldName} set to `{fieldValue}`")
  {
    Config = config;
  }

  public readonly ConnectionConfig Config;
}

public class RequestCancelledException : ConnectionException
{
  public RequestCancelledException(Connection connection, ConnectionRequestData requestData) : base(connection, $"Request has been canceled.")
  {
    RequestData = requestData;
  }

  public readonly ConnectionRequestData RequestData;
}
