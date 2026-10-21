using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ZapretPro.Core.Health;

/// <summary>
/// Сквозная проверка Telegram: подключаемся к локальному прокси так же, как Telegram Desktop
/// (MTProto obfuscated2 с секретом прокси), и просим у сервера Telegram первый шаг авторизации —
/// req_pq_multi. Пришёл resPQ с нашим nonce — Telegram доступен, каким бы путём прокси до него ни дошёл
/// (WebSocket, Cloudflare или VPN). Никаких данных аккаунта при этом не нужно и не передаётся.
/// </summary>
public static class TelegramProbe
{
    private const int HandshakeLength = 64;
    private const uint ProtoIntermediate = 0xeeeeeeee;
    private const uint ReqPqMulti = 0xbe7e8ef1;
    private const uint ResPq = 0x05162463;

    /// <summary>Null — всё хорошо, иначе — причина для журнала.</summary>
    public static async Task<string?> CheckAsync(string host, int port, string secretHex, short dc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, port, cts.Token);
            }
            catch (SocketException)
            {
                return $"прокси не слушает порт {port}";
            }

            var stream = client.GetStream();
            var (init, encryptor, decryptor) = CreateHandshake(Convert.FromHexString(secretHex), dc);
            await stream.WriteAsync(init, cts.Token);

            var nonce = RandomNumberGenerator.GetBytes(16);
            var packet = encryptor.Transform(Frame(ReqPqMultiMessage(nonce)));
            await stream.WriteAsync(packet, cts.Token);

            var lengthBytes = decryptor.Transform(await ReadExactlyAsync(stream, 4, cts.Token));
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 4 or > 4096)
                return $"странный ответ от Telegram (длина {length})";

            var payload = decryptor.Transform(await ReadExactlyAsync(stream, length, cts.Token));
            if (length == 4)
                return $"Telegram ответил ошибкой {BinaryPrimitives.ReadInt32LittleEndian(payload)}";
            if (length < 20 + 4 + 16
                || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20)) != ResPq
                || !payload.AsSpan(24, 16).SequenceEqual(nonce))
                return "от Telegram пришёл непонятный ответ";

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Telegram не ответил за {timeout.TotalSeconds:0} с";
        }
        catch (Exception e) when (e is IOException or SocketException or EndOfStreamException)
        {
            return "прокси закрыл соединение — не смог достучаться до Telegram";
        }
    }

    /// <summary>
    /// Начало соединения obfuscated2 (как у клиента Telegram): 64 случайных байта, в 56..60 — тег
    /// протокола, в 60..62 — номер дата-центра. Ключи: SHA-256(байты 8..40 + секрет), IV — байты 40..56;
    /// для обратного направления — то же по развёрнутым байтам 8..56.
    /// </summary>
    internal static (byte[] Init, AesCtr Encryptor, AesCtr Decryptor) CreateHandshake(byte[] secret, short dc)
    {
        var init = new byte[HandshakeLength];
        do
        {
            RandomNumberGenerator.Fill(init);
        } while (!IsAcceptableStart(init));

        BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(56), ProtoIntermediate);
        BinaryPrimitives.WriteInt16LittleEndian(init.AsSpan(60), dc);

        var encryptor = new AesCtr(SHA256.HashData([.. init.AsSpan(8, 32), .. secret]), init.AsSpan(40, 16).ToArray());
        var reversed = init.AsSpan(8, 48).ToArray();
        Array.Reverse(reversed);
        var decryptor = new AesCtr(SHA256.HashData([.. reversed.AsSpan(0, 32), .. secret]), reversed.AsSpan(32, 16).ToArray());

        // Начало уходит открытым, а тег и номер дата-центра — зашифрованными
        var encrypted = encryptor.Transform(init);
        init.AsSpan(0, 56).CopyTo(encrypted);
        return (encrypted, encryptor, decryptor);
    }

    /// <summary>Начало не должно выглядеть как HTTP, TLS или другой транспорт MTProto.</summary>
    private static bool IsAcceptableStart(byte[] init)
    {
        if (init[0] is 0xef or 0x16)
            return false;
        var first = BinaryPrimitives.ReadUInt32LittleEndian(init);
        if (first is 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or 0xdddddddd or 0xeeeeeeee or 0x02010316)
            return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(4)) != 0;
    }

    /// <summary>Незашифрованное сообщение MTProto: auth_key_id = 0, msg_id, длина, req_pq_multi(nonce).</summary>
    internal static byte[] ReqPqMultiMessage(byte[] nonce)
    {
        var message = new byte[8 + 8 + 4 + 4 + 16];
        var now = DateTimeOffset.UtcNow;
        // msg_id ≈ unixtime·2³², кратен 4
        var msgId = (now.ToUnixTimeSeconds() << 32) | ((long)now.Millisecond * 4294967 & 0xfffffffc);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(8), msgId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(16), 4 + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), ReqPqMulti);
        nonce.CopyTo(message, 24);
        return message;
    }

    /// <summary>Транспорт intermediate: 4 байта длины + данные.</summary>
    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}

/// <summary>AES-256 в режиме CTR (счётчик big-endian, как в MTProto). В .NET встроенного CTR нет.</summary>
internal sealed class AesCtr
{
    private readonly Aes _aes;
    private readonly byte[] _counter;
    private readonly byte[] _keystream = new byte[16];
    private int _used = 16;

    public AesCtr(byte[] key, byte[] iv)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        _counter = (byte[])iv.Clone();
    }

    public byte[] Transform(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            if (_used == 16)
            {
                _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                Increment();
                _used = 0;
            }
            output[i] = (byte)(data[i] ^ _keystream[_used++]);
        }
        return output;
    }

    private void Increment()
    {
        for (var i = _counter.Length - 1; i >= 0; i--)
        {
            if (++_counter[i] != 0)
                break;
        }
    }
}
