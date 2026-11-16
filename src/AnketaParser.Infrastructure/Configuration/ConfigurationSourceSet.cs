using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Источники итоговой конфигурации.</summary>
/// <param name="Sources">Источники в порядке возрастания приоритета.</param>
public sealed record ConfigurationSourceSet(IReadOnlyList<ConfigurationSourceInfo> Sources);