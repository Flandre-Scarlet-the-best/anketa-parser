namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Среда хостинга, определённая при создании приложения.</summary>
/// <param name="Name">Среда, например <c>Production</c> или <c>Development</c>.</param>
public sealed record EnvironmentDescriptor(string Name);