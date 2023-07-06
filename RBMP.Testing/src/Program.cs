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
        try
        {
          ServerConnection(connection);
        }
        catch (Exception exception)
        {
          Console.WriteLine(exception);
        }
      });
    }
  }

  public static void ServerConnection(Connection connection)
  {
    while (connection.IsConnected)
    {
      byte[] message = connection.ReceiveMessage();

      Console.Write(System.Text.Encoding.Default.GetString(message));
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

    while (connection.IsConnected)
    {
      ConsoleKeyInfo info = Console.ReadKey(true);
      char character = info.Key == ConsoleKey.Enter ? '\n' : info.KeyChar;

      connection.SendMessage(new byte[] { ((byte)character) }, 0, 1);
      Console.Write(character);
    }

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
