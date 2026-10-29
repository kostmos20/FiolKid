using System.Text;
using ZapretPro.Core.Key;

// Показывает, какой протокол внутри ключа vpn://. Сам ключ и его настройки не выводятся.
// Ключ читается из stdin или вводится без эха: в аргументах он попал бы в историю команд.
//
//   ПроверитьКлюч.exe                 (двойной щелчок: вставить ключ, нажать Enter)
//   Get-Clipboard | ПроверитьКлюч.exe
//
// Код возврата: 0 — протокол поддерживается, 2 — не поддерживается, 1 — ключ не разобран.

Console.OutputEncoding = Encoding.UTF8;

var interactive = !Console.IsInputRedirected;
var key = interactive ? ReadHidden() : Console.In.ReadToEnd();

var code = Check(key);

// При запуске двойным щелчком окно иначе закроется раньше, чем успеешь прочитать результат.
if (interactive)
{
    Console.Error.WriteLine();
    Console.Error.Write("Нажми Enter, чтобы закрыть.");
    Console.ReadLine();
}
return code;

static int Check(string key)
{
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
}

static string ReadHidden()
{
    Console.Error.WriteLine("Проверка ключа. Сам ключ на экране не показывается и никуда не отправляется.");
    Console.Error.WriteLine();
    Console.Error.Write("Вставь ключ vpn:// (Ctrl+V или правая кнопка мыши) и нажми Enter: ");
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
        if (k.KeyChar != '\0')
            sb.Append(k.KeyChar);
    }
    Console.Error.WriteLine($"(получено символов: {sb.Length})");
    Console.Error.WriteLine();
    return sb.ToString();
}
