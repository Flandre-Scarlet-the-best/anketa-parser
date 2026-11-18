# Operation boundaries

Establish the process, Compose project when applicable, database role and
database name before running an operation. Read the effective configuration
sources and check the target with a read-only command. Report sanitized target
metadata. Treat a local CLI, an active Host and a CLI launched inside a container
as independent processes until their options and database have been verified.

| Intent | What to check |
| --- | --- |
| Inspect diagnostics, source decisions or queue counts | Inspect help and run the read-only command against the intended configuration. |
| Reproduce a write or source outage | Use an isolated migrated database and a sanitized fixture, keeping the original fixture intact. |
| Perform a manual live scan | Confirm authorization for that source and database. Respect a stored retry deadline and check the shared result after the scan. |
| Apply schema changes, rotate archives or restore | Follow the storage guide and require authorization covering the operation and target. CLI availability does not grant it. |

For example, a request to investigate why the Host is unhealthy permits
read-only status inspection. If the status shows `migration_required`, collect
the schema status and report the required permission before calling a migration
command. The same diagnostic request gives no authority to restore, change
database roles, or remove a Compose volume. Use the repository guides
`docs/persistence.md` and `docs/operations.md` for supported maintenance and
target verification.
