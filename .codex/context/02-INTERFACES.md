# Interface map

[Context index](INDEX.md)

Use this table to find a behavior owner, its regression evidence and the human
explanation. Read the actual implementation before changing a public contract;
this map deliberately omits full schemas, settings and command tables.

| Contract / question | Implementation | Regression evidence | Human guide |
| --- | --- | --- | --- |
| Argument forms and default command | [CommandLineParser](../../src/AnketaParser.Cli/CommandLineParser.cs), [HelpText](../../src/AnketaParser.Cli/HelpText.cs) | [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs) | [Diagnostics](../../docs/diagnostics.md) |
| JSON names, omitted nulls and output streams | [DiagnosticsDocument](../../src/AnketaParser.Cli/DiagnosticsDocument.cs), [CliApplication](../../src/AnketaParser.Cli/CliApplication.cs) | `VersionInMachineModeIsSingleJsonDocument`, doctor cases in [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs) | [Diagnostics](../../docs/diagnostics.md) |
| Exit codes | [ExitCodes](../../src/AnketaParser.Core/ExitCodes.cs), CLI and Host owners | [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs), [Host tests](../../tests/AnketaParser.Tests/HostApplicationTests.cs) | [Diagnostics](../../docs/diagnostics.md) |
| Dvach scan, decisions, durable status and queue | [SourceCommands](../../src/AnketaParser.Cli/SourceCommands.cs), [DvachScanStore](../../src/AnketaParser.Infrastructure/Persistence/DvachScanStore.cs) | [Source tests](../../tests/AnketaParser.Tests/DvachTests.cs), [PostgreSQL fault cases](../../tests/AnketaParser.DockerTests/DvachPersistenceTests.cs) | [Dvach](../../docs/dvach.md) |
| LLM profiles, decisions, leases and replay | [LlmCommands](../../src/AnketaParser.Cli/LlmCommands.cs), [SemanticContract](../../src/AnketaParser.Infrastructure/Llm/SemanticContract.cs), [queue operations](../../src/AnketaParser.Infrastructure/Persistence/ProductStore.Llm.cs) | [Contract tests](../../tests/AnketaParser.Tests/LlmContractTests.cs), [PostgreSQL tests](../../tests/AnketaParser.DockerTests/LlmPersistenceTests.cs) | [Text analysis](../../docs/llm.md) |
| Standalone HTML report over one scan snapshot | [ReportCommands](../../src/AnketaParser.Cli/ReportCommands.cs), [ReportQueries](../../src/AnketaParser.Infrastructure/Reporting/ReportQueries.cs), [SnapshotBoundary](../../src/AnketaParser.Infrastructure/Reporting/SnapshotBoundary.cs), [ReportHtmlRenderer](../../src/AnketaParser.Cli/ReportHtmlRenderer.cs) | [Report tests](../../tests/AnketaParser.Tests/ReportTests.cs), [command tests](../../tests/AnketaParser.Tests/ReportCommandTests.cs) | [Report](../../docs/report.md) |
| Source precedence and environment selection | [AnketaParserConfiguration](../../src/AnketaParser.Infrastructure/Configuration/AnketaParserConfiguration.cs) | [Configuration tests](../../tests/AnketaParser.Tests/ConfigurationTests.cs) | [Configuration](../../docs/configuration.md) |
| Settings, ranges and effective defaults | [Settings and validators](../../src/AnketaParser.Infrastructure/Configuration/), [Host JSON](../../src/AnketaParser.Host/appsettings.json), [CLI JSON](../../src/AnketaParser.Cli/appsettings.json) | [Configuration tests](../../tests/AnketaParser.Tests/ConfigurationTests.cs) | [Configuration](../../docs/configuration.md) |
| Probe timeout, cancellation, aggregation | [DiagnosticsRunner](../../src/AnketaParser.Core/Diagnostics/DiagnosticsRunner.cs) | [Runner tests](../../tests/AnketaParser.Tests/DiagnosticsRunnerTests.cs) | [Diagnostics](../../docs/diagnostics.md) |
| PostgreSQL connectivity and failure classification | [PostgresReadinessProbe](../../src/AnketaParser.Infrastructure/Persistence/PostgresReadinessProbe.cs) | CLI unreachable-DB case; [live smoke](../../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs) only when enabled | [Operations](../../docs/operations.md) |
| Startup policy, monitor and in-memory state | [StartupDiagnosticsService](../../src/AnketaParser.Host/StartupDiagnosticsService.cs), [ReadinessMonitorService](../../src/AnketaParser.Host/ReadinessMonitorService.cs), [ReadinessStateStore](../../src/AnketaParser.Infrastructure/Diagnostics/ReadinessStateStore.cs) | [Host tests](../../tests/AnketaParser.Tests/HostApplicationTests.cs); not every monitor transition has a dedicated test | [Diagnostics](../../docs/diagnostics.md) |
| Runtime snapshot | [RuntimeSnapshotProvider](../../src/AnketaParser.Infrastructure/Diagnostics/RuntimeSnapshotProvider.cs), [RuntimeSnapshot](../../src/AnketaParser.Core/Diagnostics/RuntimeSnapshot.cs) | Doctor JSON assertions in [CLI tests](../../tests/AnketaParser.Tests/CliApplicationTests.cs) | [Diagnostics](../../docs/diagnostics.md) |
| Address masking and log limitations | [ConnectionStringDescriptor](../../src/AnketaParser.Infrastructure/Configuration/ConnectionStringDescriptor.cs), probe logger and application exception handlers | [Descriptor tests](../../tests/AnketaParser.Tests/ConnectionStringDescriptorTests.cs), runner and CLI failure cases | [Security](../../docs/security.md) |

## Interpretation traps

No arguments selects help. CLI readiness failure and Host startup policy differ:
inspect both before interpreting `Database:Required`. The periodic monitor does
not terminate Host on dependency loss. Docker health runs a separate CLI process,
not a query of Host memory. Missing files are allowed; malformed JSON and
unexpected exceptions do not necessarily yield a diagnostic JSON body.
Tests are evidence for their assertions, not complete interface coverage.
`report html` writes no file when the scan snapshot cannot be proved from one
completed scan; its diagnostic JSON then carries the actual counters instead.

## Product storage

[Storage contracts](../../src/AnketaParser.Core/Persistence/StorageContracts.cs)
route to [ProductStore](../../src/AnketaParser.Infrastructure/Persistence/ProductStore.cs),
[model and migrations](../../src/AnketaParser.Infrastructure/Persistence/) and
[maintenance commands](../../src/AnketaParser.Cli/StorageCommands.cs).
[Persistence tests](../../tests/AnketaParser.DockerTests/ProductPersistenceTests.cs)
and [recovery tests](../../tests/AnketaParser.DockerTests/BackupRecoveryTests.cs)
require explicit Docker opt-in. [Backup unit tests](../../tests/AnketaParser.Tests/BackupServiceTests.cs)
cover retention and local failures. Human owner: [storage guide](../../docs/persistence.md).
