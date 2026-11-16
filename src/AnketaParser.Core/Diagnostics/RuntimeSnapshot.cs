namespace AnketaParser.Core.Diagnostics;

/// <summary>Сведения о среде выполнения без секретных значений.</summary>
/// <param name="FrameworkDescription">Описание среды .NET, например <c>.NET 10.0.14</c>.</param>
/// <param name="OsDescription">Описание операционной системы.</param>
/// <param name="Architecture">Архитектура процесса.</param>
/// <param name="IsContainer">Признак запуска в контейнере.</param>
/// <param name="MachineName">Имя машины или хоста контейнера.</param>
/// <param name="ProcessId">Идентификатор процесса.</param>
/// <param name="Uptime">Время работы процесса, если известно.</param>
/// <param name="InstanceName">Имя экземпляра для различения хостов в логах.</param>
/// <param name="EnvironmentName">Фактическая среда хостинга.</param>
public sealed record RuntimeSnapshot(
    string FrameworkDescription,
    string OsDescription,
    string Architecture,
    bool IsContainer,
    string MachineName,
    int ProcessId,
    TimeSpan? Uptime,
    string InstanceName,
    string EnvironmentName);