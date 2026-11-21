namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Диагностический отказ построения отчёта: категория вместо сообщения исключения и
/// фактические значения, объясняющие причину. Сообщение остаётся равным категории,
/// чтобы в диагностику не попадали данные хранилища.
/// </summary>
public sealed class ReportDiagnosticException : Exception
{
    public ReportDiagnosticException(string category, IReadOnlyList<ReportDiagnosticFact>? facts = null)
        : base(category)
    {
        Category = category;
        Facts = facts ?? [];
    }

    /// <summary>Стабильная категория отказа.</summary>
    public string Category { get; }

    /// <summary>Фактические значения, на которых обнаружен отказ.</summary>
    public IReadOnlyList<ReportDiagnosticFact> Facts { get; }
}

/// <summary>Именованный факт отказа: имя и значение в виде строки.</summary>
public sealed record ReportDiagnosticFact(string Name, string Value);