using System.Diagnostics;

namespace ZapretPro.Core.Infrastructure;

/// <summary>
/// Хранит последние строки вывода процесса для отчёта. Вывод нужно вычитывать постоянно,
/// иначе переполнится буфер и процесс зависнет на записи.
/// </summary>
public sealed class OutputTail
{
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;

    public OutputTail(int capacity = 30)
    {
        _capacity = capacity;
    }

    public void Attach(Process process)
    {
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public void Clear()
    {
        lock (_lines)
            _lines.Clear();
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToArray();
        }
    }

    private void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
                _lines.Dequeue();
        }
    }
}
