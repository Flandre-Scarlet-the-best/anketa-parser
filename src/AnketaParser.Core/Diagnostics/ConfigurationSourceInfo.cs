namespace AnketaParser.Core.Diagnostics;

/// <summary>Один источник итоговой конфигурации.</summary>
/// <param name="Name">Название источника, например <c>appsettings.json</c>.</param>
/// <param name="Kind">Тип источника: <c>file</c>, <c>environment</c> или <c>override</c>.</param>
public sealed record ConfigurationSourceInfo(string Name, string Kind);