namespace AnketaParser.Core.Diagnostics;

/// <summary>One configuration source that actually contributed to the effective settings.</summary>
/// <param name="Name">Human readable source name, for example <c>appsettings.json</c>.</param>
/// <param name="Kind">Source kind: <c>file</c>, <c>environment</c> or <c>override</c>.</param>
public sealed record ConfigurationSourceInfo(string Name, string Kind);