using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Configuration sources that contributed to the effective settings.</summary>
/// <param name="Sources">Sources in increasing precedence order.</param>
public sealed record ConfigurationSourceSet(IReadOnlyList<ConfigurationSourceInfo> Sources);