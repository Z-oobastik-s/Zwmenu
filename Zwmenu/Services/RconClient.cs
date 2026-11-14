using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zwmenu.Services;

/// <summary>Короткий клиент RCON Minecraft: авторизация и одна команда.</summary>
static class RconClient
{
    private const int Auth = 3;
    private const int Exec = 2;

    public static Task<(bool Ok, string Text)> ExecuteAsync(string host, int port, string password, string command) =>
        Task.Run(() => Execute(host, port, password, command));

    private static (bool Ok, string Text) Execute(string host, int port, string password, string command)
    {
        host = (host ?? "").Trim();
        command = (command ?? "").Trim();
        if (host.Length == 0)
            return (false, "Не указан адрес сервера.");
        if (port is < 1 or > 65535)
            return (false, "Порт RCON указан неверно.");
        if (command.Length == 0)
            return (false, "Пустая команда.");

        Socket? socket = null;
        try
        {
            socket = Connect(host, port, TimeSpan.FromSeconds(12));
            int authId = Random.Shared.Next(1, int.MaxValue);
            Send(socket, authId, Auth, password ?? "");
            var authReply = Read(socket);
            if (authReply.Id == -1)
                return (false, "Сервер не принял пароль RCON.");

            int cmdId = Random.Shared.Next(1, int.MaxValue);
            Send(socket, cmdId, Exec, command);
            var reply = Read(socket);
            string text = reply.Body.Trim();
            socket.ReceiveTimeout = 350;
            try
            {
                var extra = Read(socket);
                if (extra.Body.Trim().Length > 0)
                    text = text.Length == 0 ? extra.Body.Trim() : text + "\n" + extra.Body.Trim();
            }
            catch (SocketException)
            {
                // Второго куска ответа нет.
            }

            if (text.Length == 0)
                text = "Команда ушла, сервер ничего не ответил.";
            return (true, text);
        }
        catch (Exception ex)
        {
            return (false, "RCON: " + ex.Message);
        }
        finally
        {
            try { socket?.Dispose(); } catch { /* сокет уже закрыт */ }
        }
    }

    private static Socket Connect(string host, int port, TimeSpan timeout)
    {
        Exception? last = null;
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Имя сервера не находится: " + ex.Message);
        }

        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            foreach (var address in addresses)
            {
                if (address.AddressFamily != family)
                    continue;
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveTimeout = (int)timeout.TotalMilliseconds,
                    SendTimeout = (int)timeout.TotalMilliseconds
                };
                try
                {
                    var done = socket.ConnectAsync(address, port);
                    if (!done.Wait(timeout))
                    {
                        socket.Dispose();
                        last = new TimeoutException("Сервер не ответил.");
                        continue;
                    }

                    return socket;
                }
                catch (Exception ex)
                {
                    last = ex;
                    socket.Dispose();
                }
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Не удалось подключиться.");
    }

    private static void Send(Socket socket, int id, int type, string body)
    {
        byte[] text = Encoding.UTF8.GetBytes(body);
        var payload = new byte[8 + text.Length + 2];
        BitConverter.GetBytes(id).CopyTo(payload, 0);
        BitConverter.GetBytes(type).CopyTo(payload, 4);
        text.CopyTo(payload, 8);
        var packet = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(packet, 0);
        payload.CopyTo(packet, 4);
        socket.Send(packet);
    }

    private static (int Id, string Body) Read(Socket socket)
    {
        byte[] lenBuf = ReadExact(socket, 4);
        int length = BitConverter.ToInt32(lenBuf, 0);
        if (length < 10 || length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Сервер прислал непонятный ответ.");
        byte[] data = ReadExact(socket, length);
        int id = BitConverter.ToInt32(data, 0);
        int end = data.Length;
        while (end > 8 && data[end - 1] == 0)
            end--;
        string body = Encoding.UTF8.GetString(data, 8, Math.Max(0, end - 8));
        return (id, body);
    }

    private static byte[] ReadExact(Socket socket, int count)
    {
        var buffer = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = socket.Receive(buffer, got, count - got, SocketFlags.None);
            if (n <= 0)
                throw new InvalidOperationException("Сервер закрыл соединение.");
            got += n;
        }

        return buffer;
    }
}
