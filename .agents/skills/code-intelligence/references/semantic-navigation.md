# Semantic navigation

Identify the repository root, actual worktree, language and changed scope. Inspect
available tool schemas and server state rather than inferring capabilities from a
harness name. Use file search and reading for paths, text and simple orientation;
semantic calls are useful for definitions, hover, implementations and references
when resolving symbol identity or impact, not for an obvious Markdown edit.

Before changing a public interface, removing a symbol, substantial movement or
rename, inspect its definition, relevant implementations and usages. Account for
dynamic calls, generated code, external consumers and the supported index scope.
An empty or truncated response, stale index or unsupported operation does not prove
absence of uses. If semantic navigation is unavailable, combine scoped search and
reading with compiler and relevant tests; report that semantic coverage is unknown.

For multiple worktrees or subagents, verify that the server serves the intended root
and current contents before relying on results. An active different Serena project
or unsynchronized index is not evidence for this worktree. Choose an isolated server
or safe sharing based on actual session state and access policy; do not assume one
server per agent is always necessary. Avoid switching a shared session while another
agent relies on it. Record root, operation and relevant coverage or failure.
