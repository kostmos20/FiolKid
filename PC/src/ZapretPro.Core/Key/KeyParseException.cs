namespace ZapretPro.Core.Key;

/// <summary>
/// Ключ не удалось разобрать. <see cref="Exception.Message"/> — понятный текст для пользователя,
/// в нём никогда нет содержимого ключа.
/// </summary>
public sealed class KeyParseException : Exception
{
    public KeyParseException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
