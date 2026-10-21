using System.Text;
using ZapretPro.Core.Key;

// Показывает, какой протокол внутри ключа vpn://. Сам ключ и его настройки не выводятся.
// Ключ читается из stdin или вводится без эха: в аргументах он попал бы в историю команд.
//
//   Get-Clipboard | OdnaKnopka.KeyTool.exe
//   OdnaKnopka.KeyTool.exe            (вставить ключ и нажать Enter)
//
// Код возврата: 0 — протокол поддерживается, 2 — не поддерживается, 1 — ключ не разобран.

Console.OutputEncoding = Encoding.UTF8;

var key = Console.IsInputRedirected ? Console.In.ReadToEnd() : ReadHidden();

try
{
    var parsed = KeyParser.Parse(key);
    Console.WriteLine($"Протокол: {VpnProtocolInfo.DisplayName(parsed.Protocol)} ({parsed.ContainerName})");
    if (parsed.AllContainers.Count > 1)
        Console.WriteLine($"Все контейнеры в ключе: {string.Join(", ", parsed.AllContainers)}");

    if (parsed.IsSupported)
    {
        Console.WriteLine("Поддерживается: да");
        return 0;
    }

    Console.WriteLine($"Поддерживается: нет. {parsed.UnsupportedReason}");
    return 2;
}
catch (KeyParseException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string ReadHidden()
{
    Console.Error.Write("Вставь ключ vpn:// и нажми Enter: ");
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter)
            break;
        if (k.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
                sb.Length--;
            continue;
        }
        sb.Append(k.KeyChar);
    }
    Console.Error.WriteLine($"(получено символов: {sb.Length})");
    return sb.ToString();
}
