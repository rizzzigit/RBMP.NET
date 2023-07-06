using System.Net.WebSockets;

namespace RizzziGit.RBMP;

internal class WebSocketStreamBridge : Stream
{
  public WebSocketStreamBridge(WebSocket websocket)
  {
    WebSocket = websocket;
  }

  private WebSocket WebSocket;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotImplementedException();
  public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
  public override void SetLength(long value) => throw new NotImplementedException();

  public override void Close()
  {
    base.Close();

    if (!(new WebSocketState[] { WebSocketState.Aborted, WebSocketState.Closed, WebSocketState.CloseSent }).Contains(WebSocket.State))
    {
      return;
    }

    try
    {
      WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new(false)).Wait();
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    if (
      (WebSocket.State == WebSocketState.Closed) ||
      (WebSocket.State == WebSocketState.Aborted)
    )
    {
      return 0;
    }

    var task = WebSocket.ReceiveAsync(new(buffer, offset, count), new(false));

    try
    {
      task.Wait();

      if (task.Result.CloseStatus != null)
      {
        try { Close(); } catch {}
      }

      return task.Result.Count;
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    var task = WebSocket.SendAsync(new(buffer, offset, count), WebSocketMessageType.Binary, false, new(false));

    try
    {
      task.Wait();
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }
}
