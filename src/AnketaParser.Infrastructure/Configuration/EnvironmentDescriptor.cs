namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Effective hosting environment name, resolved once per process.</summary>
/// <param name="Name">Environment name such as <c>Production</c> or <c>Development</c>.</param>
public sealed record EnvironmentDescriptor(string Name);