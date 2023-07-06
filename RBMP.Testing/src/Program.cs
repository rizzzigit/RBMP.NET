﻿using System.Diagnostics;
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
    TcpListener listener = new(IPEndPoint.Parse(address));
    listener.Start();

    while (true)
    {
      TcpClient client = listener.AcceptTcpClient();
      WebSocket websocket = WebSocket.CreateFromStream(client.GetStream(), true, null, TimeSpan.Zero);
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
      try
      {
        byte[] message = connection.ReceiveMessage();

        Console.Write(System.Text.Encoding.Default.GetString(message));
      }
      catch (Exception exception)
      {
        Console.WriteLine(exception);
      }
    }
  }

  public static void Client(string address)
  {
    TcpClient client = new();
    client.Connect(IPEndPoint.Parse(address));
    WebSocket webSocket = WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.Zero);
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
