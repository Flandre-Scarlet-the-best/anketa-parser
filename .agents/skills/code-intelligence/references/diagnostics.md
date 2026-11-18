# Diagnostics

After editing code, request fresh diagnostics for the affected files when an
available tool supports them and serves this worktree and current contents. Inspect
severity, scope, freshness and omitted files. A server error, unsupported operation,
stale result or empty response with unknown coverage is not a clean project.
Corroborate disputed diagnostics with the compiler, relevant analyzer or focused
test. If no diagnostic capability is available, use the appropriate CLI checks
and report their scope; do not substitute hover for diagnostics.
