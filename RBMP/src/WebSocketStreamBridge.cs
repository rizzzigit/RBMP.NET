using System.Net.WebSockets;

namespace RizzziGit.RBMP;

internal class WebSocketStreamBridge : Stream
{
  public WebSocketStreamBridge(WebSocket websocket)
  {
    WebSocket = websocket;

    MessageSegmentLength = ConnectionConfig.ConfigBufferSize;
    MessageSegmentPartialBuffer = new byte[4];
    MessageSegmentPartiaBufferPosition = 0;
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
    ReceiveCancellationTokenSource?.Cancel();

    if ((new WebSocketState[] { WebSocketState.Aborted, WebSocketState.Closed, WebSocketState.CloseSent }).Contains(WebSocket.State))
    {
      return;
    }

    try
    {
      try { _Write(new byte[0], 0, 0, true); }
      catch { }

      WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new(false)).Wait();
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }

  private CancellationTokenSource? ReceiveCancellationTokenSource;
  public override int Read(byte[] buffer, int offset, int count)
  {
    if (
      (WebSocket.State == WebSocketState.Closed) ||
      (WebSocket.State == WebSocketState.Aborted)
    )
    {
      return 0;
    }

    var task = WebSocket.ReceiveAsync(new(buffer, offset, count), (ReceiveCancellationTokenSource = new()).Token);

    try
    {
      task.Wait();

      if (task.Result.CloseStatus != null)
      {
        try { Close(); } catch { }
      }

      return task.Result.Count;
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }

  private void _Write(byte[] buffer, int offset, int count, bool flush)
  {
    var task = WebSocket.SendAsync(new(buffer, offset, count), WebSocketMessageType.Binary, flush, new(false));

    try
    {
      task.Wait();
    }
    catch (AggregateException exception)
    {
      throw exception.GetBaseException();
    }
  }

  private int MessageSegmentLength;
  private byte[] MessageSegmentPartialBuffer;
  private int MessageSegmentPartiaBufferPosition;
  private int MessageSegmentPartialBufferRemaining => 4 - MessageSegmentPartiaBufferPosition;
  public override void Write(byte[] buffer, int offset, int count)
  {
    if (count == 0)
    {
      return;
    }

    if (MessageSegmentLength == 0)
    {
      int index = 0;
      for (; (MessageSegmentPartialBufferRemaining > 0) && (index < count); index++)
      {
        MessageSegmentPartialBuffer[MessageSegmentPartiaBufferPosition] = buffer[index + offset];
        MessageSegmentPartiaBufferPosition++;
      }

      if (MessageSegmentPartialBufferRemaining == 0)
      {
        MessageSegmentPartiaBufferPosition = 0;
        MessageSegmentLength = index + BitConverter.ToInt32(MessageSegmentPartialBuffer);
      }
    }

    if ((MessageSegmentLength == 0 || MessageSegmentLength > count))
    {
      _Write(buffer, offset, count, false);

      if (MessageSegmentLength != 0)
      {
        MessageSegmentLength -= count;
      }
    }
    else
    {
      _Write(buffer, offset, MessageSegmentLength, true);
      var messageSegmentLength = MessageSegmentLength;
      MessageSegmentLength = 0;

      Write(buffer, offset + messageSegmentLength, count - messageSegmentLength);
    }
  }
}
