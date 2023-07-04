using System.Diagnostics;

namespace RizzziGit.RBMP.Testing;

using Buffer;

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
  }

  public static void ServerConnection(Connection connection)
  {
  }

  public static void Client(string address)
  {
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
