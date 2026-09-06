using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>Loopback UDP/TCP senders and a free-port helper for the ingestion tests.</summary>
public static class LoopbackSyslog
{
    public static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    public static int FreeTcpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Sends <paramref name="count"/> UDP datagrams to the port, each a small distinct
    /// message. Paced in bursts so the loopback socket buffer is not overrun — the test is
    /// about the pipeline's zero-loss guarantee, not the OS UDP buffer.
    /// </summary>
    public static async Task SendUdpAsync(int port, int count, string prefix = "msg", int burst = 500)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.SendBufferSize = 8 * 1024 * 1024;
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        for (int i = 0; i < count; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"<13>{prefix}-{i}");
            await client.SendToAsync(payload, SocketFlags.None, endpoint);
            if (burst > 0 && i % burst == burst - 1)
            {
                await Task.Delay(1);
            }
        }
    }

    public static async Task SendUdpRawAsync(int port, IEnumerable<byte[]> datagrams)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        foreach (byte[] d in datagrams)
        {
            await client.SendToAsync(d, SocketFlags.None, endpoint);
        }
    }

    /// <summary>Opens a TCP connection and sends <paramref name="count"/> newline-framed messages.</summary>
    public static async Task SendTcpNewlineAsync(int port, int count, string prefix = "msg")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append("<13>").Append(prefix).Append('-').Append(i).Append('\n');
            if (sb.Length > 32 * 1024)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        await stream.FlushAsync();
    }

    /// <summary>Opens a TCP connection and sends <paramref name="count"/> RFC 6587 octet-counted messages.</summary>
    public static async Task SendTcpOctetCountedAsync(int port, int count, string prefix = "msg")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();
        var buffer = new MemoryStream();
        for (int i = 0; i < count; i++)
        {
            byte[] msg = Encoding.UTF8.GetBytes($"<13>{prefix}-{i}");
            byte[] header = Encoding.ASCII.GetBytes($"{msg.Length} ");
            buffer.Write(header);
            buffer.Write(msg);
            if (buffer.Length > 32 * 1024)
            {
                await stream.WriteAsync(buffer.ToArray());
                buffer.SetLength(0);
            }
        }

        if (buffer.Length > 0)
        {
            await stream.WriteAsync(buffer.ToArray());
        }

        await stream.FlushAsync();
    }

    public static byte[] OctetCounted(string message)
    {
        byte[] msg = Encoding.UTF8.GetBytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"{msg.Length} ");
        byte[] result = new byte[header.Length + msg.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(msg, 0, result, header.Length, msg.Length);
        return result;
    }
}
