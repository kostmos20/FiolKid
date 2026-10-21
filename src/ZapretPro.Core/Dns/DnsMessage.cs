using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ZapretPro.Core.Dns;

/// <summary>
/// Минимум работы с DNS-сообщениями (RFC 1035), который нужен нашему DNS: прочитать вопрос,
/// достать A-записи из ответа и собрать пустой ответ. Всё остальное пересылается как есть.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort TypeHttps = 65;
    private const int HeaderLength = 12;

    /// <summary>Имя и тип первого вопроса. False — сообщение не похоже на обычный DNS-запрос.</summary>
    public static bool TryReadQuestion(ReadOnlySpan<byte> message, out string name, out ushort type, out int questionEnd)
    {
        name = "";
        type = 0;
        questionEnd = 0;
        if (message.Length < HeaderLength + 5 || BinaryPrimitives.ReadUInt16BigEndian(message[4..]) == 0)
            return false;

        var offset = HeaderLength;
        var sb = new StringBuilder();
        while (true)
        {
            if (offset >= message.Length)
                return false;
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                break;
            }
            // В вопросе сжатие имён не используется; метка длиннее 63 — не DNS
            if (length > 63 || offset + 1 + length > message.Length)
                return false;
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(message.Slice(offset + 1, length)));
            offset += 1 + length;
        }

        if (offset + 4 > message.Length)
            return false;
        type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
        questionEnd = offset + 4;
        name = sb.ToString().ToLowerInvariant().TrimEnd('.');
        return name.Length > 0;
    }

    /// <summary>Ответ «ошибок нет, но и записей нет» на этот запрос — так браузер не пойдёт по IPv6 мимо VPN.</summary>
    public static byte[] EmptyAnswer(ReadOnlySpan<byte> query)
    {
        if (!TryReadQuestion(query, out _, out _, out var questionEnd))
            throw new ArgumentException("Это не DNS-запрос", nameof(query));
        var answer = query[..questionEnd].ToArray();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2));
        // QR=1 (ответ), opcode и RD как в запросе, RA=1, RCODE=0
        flags = (ushort)(0x8000 | (flags & 0x7900) | 0x0080);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), 1);
        answer.AsSpan(6, 6).Clear();
        return answer;
    }

    /// <summary>Ответ «сервер не смог» — если не ответил ни один из вышестоящих DNS.</summary>
    public static byte[] ServerFailure(ReadOnlySpan<byte> query)
    {
        var answer = EmptyAnswer(query);
        answer[3] = (byte)((answer[3] & 0xF0) | 2);
        return answer;
    }

    public static ushort Id(ReadOnlySpan<byte> message) => BinaryPrimitives.ReadUInt16BigEndian(message);

    /// <summary>Флаг TC: ответ обрезан, его нужно запросить по TCP.</summary>
    public static bool IsTruncated(ReadOnlySpan<byte> message) => message.Length >= 3 && (message[2] & 0x02) != 0;

    /// <summary>IPv4-адреса из A-записей ответа (включая записи после CNAME).</summary>
    public static IReadOnlyList<IPAddress> ReadARecords(ReadOnlySpan<byte> message)
    {
        var result = new List<IPAddress>();
        if (message.Length < HeaderLength)
            return result;

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var offset = HeaderLength;
        for (var i = 0; i < questions; i++)
        {
            if (!SkipName(message, ref offset) || offset + 4 > message.Length)
                return result;
            offset += 4;
        }

        for (var i = 0; i < answers; i++)
        {
            if (!SkipName(message, ref offset) || offset + 10 > message.Length)
                return result;
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;
            if (offset + length > message.Length)
                return result;
            if (type == TypeA && length == 4)
                result.Add(new IPAddress(message.Slice(offset, 4)));
            offset += length;
        }
        return result;
    }

    private static bool SkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                return true;
            }
            // Ссылка на имя выше по сообщению (сжатие): 2 байта, имя на этом заканчивается
            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= message.Length;
            }
            offset += 1 + length;
        }
        return false;
    }
}
