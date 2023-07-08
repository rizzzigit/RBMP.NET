using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;

namespace RizzziGit.RBMP.Testing;

public static class Program
{
  private static string[] Units = new string[] { "", "K", "M", "G", "T" };
  public static string ToReadable(int value) => ToReadable((double)value);
  public static string ToReadable(double value)
  {
    int unit = 0;

    while (value > 999)
    {
      value /= 1024;
      unit++;
    }

    return $"{Math.Round(value, 2)}{Units[unit]}B";
  }

  public static void Server(string address)
  {
    HttpListener listener = new();
    listener.Prefixes.Add("http://+:8080/");
    listener.Start();

    while (true)
    {
      HttpListenerContext context = listener.GetContext();
      if (!context.Request.IsWebSocketRequest)
      {
        context.Response.StatusCode = 400;
        context.Response.Close();
        continue;
      }

      Task<HttpListenerWebSocketContext> webSocketContext = context.AcceptWebSocketAsync(null);
      webSocketContext.Wait();

      WebSocket websocket = webSocketContext.Result.WebSocket;
      Connection connection = new Connection(new(50UL), websocket);

      Task.Run(() =>
      {
        Monitor monitor = new();
        monitor.Start();
        try
        {
          ServerConnection(connection, monitor);
        }
        catch (Exception exception)
        {
          Console.WriteLine(exception);
        }
        monitor.Stop();
      });
    }
  }

  public class Monitor
  {
    public Monitor()
    {
      Count = 0;

      Running = false;
    }

    public ulong Count;
    public void Add(int count)
    {
      lock (this)
      {
        Count += (ulong)count;
      }
    }

    public bool Running { get; private set; }
    public void Start() => Task.Run(() =>
    {
      if (Running)
      {
        return;
      }

      Running = true;
      try
      {
        while (Running)
        {
          lock (this)
          {
            if (Count != 0)
            {
              Console.WriteLine($"Rate: {ToReadable(Count)}/s");
            }

            Count = 0;
          }
          Thread.Sleep(1000);
        }
      }
      finally
      {
        Running = false;
      }
    });

    public void Stop() => Running = false;
  }

  public static void ServerConnection(Connection connection, Monitor monitor)
  {
    byte[] buffer = new byte[256 * 1024];
    while (connection.IsConnected)
    {
      ConnectionRequestData message = connection.ReceiveRequest();
      Console.WriteLine($"Request #{message.ID}: Cancelled: {message.CancellationToken.IsCancellationRequested}");
      Thread.Sleep(1000);
      Console.WriteLine($"Request #{message.ID}: Cancelled: {message.CancellationToken.IsCancellationRequested}");

      // Console.Write(System.Text.Encoding.Default.GetString(message));
    }
  }

  public static void ClientConnection(Connection connection, Monitor monitor)
  {
    byte[] buffer = new byte[32];

    while (connection.IsConnected)
    {
      CancellationTokenSource source = new();
      Task<ConnectionResponseData> responseData = connection.SendRequestAsync(50, new byte[0], 0, 0, source.Token);
      Thread.Sleep(500);
      source.Cancel();

      Thread.Sleep(5000);
    }
  }

  public static void Client(string address)
  {
    ClientWebSocket webSocket = new();
    webSocket.Options.AddSubProtocol("permessage-deflate");
    webSocket.ConnectAsync(new Uri(address), new(false)).Wait();
    Connection connection = new Connection(new(51UL), webSocket);

    Console.CancelKeyPress += (sender, args) =>
    {
      connection.Disconnect();
    };

    Monitor monitor = new();
    monitor.Start();
    ClientConnection(connection, monitor);
    monitor.Stop();

    connection.Disconnect();
  }

  public static Process? StartProcess(bool isServer)
  {
    ProcessStartInfo startInfo = new();

    startInfo.FileName = Process.GetCurrentProcess().MainModule?.FileName;
    var list = startInfo.ArgumentList;
    list.Add(isServer ? "server" : "client");
    list.Add("0.0.0.0:8080");

    return Process.Start(startInfo);
  }

  public static Task WaitForExit(Process? process)
  {
    TaskCompletionSource source = new();

    if (process?.HasExited ?? true)
    {
      source.SetResult();
    }
    else
    {
      process.Exited += (sender, args) => source.SetResult();
    }

    return source.Task;
  }

  public static async Task Main(string[] args)
  {
    Console.WriteLine("Program started.");
    if (args.Length != 2)
    {
      Task server = WaitForExit(StartProcess(true));
      await Task.Delay(1000);
      Task client = WaitForExit(StartProcess(false));

      await Task.WhenAll(server, client);
    }
    else if (args[0].ToLower() == "server")
    {
      Server(args[1]);
    }
    else
    {
      Client(args[1]);
    }
  }
}
