# Claude Code

Project skills are normally discovered under `.claude/skills`, not `.agents/skills`.
When the root agent instructions are loaded, their short route points to the
canonical skill; this is an instruction-based route, not native skill discovery.
If project instructions are not loaded, open the canonical skill explicitly.
Claude Code's reading of `AGENTS.md` depends on version, project instruction
settings and nearby `CLAUDE.md` files. Worktree skill discovery may also fall
back to the main checkout. Verify the path and loaded instructions in the actual
session; do not assume a different checkout's skill describes this worktree.

Code Intelligence/LSP operations require an enabled, compatible plugin and
language server. Inspect available tools and actual responses before relying on
navigation or diagnostics; apply the common fallback when unavailable. See
[skills](https://code.claude.com/docs/en/skills) and
[project instructions](https://code.claude.com/docs/en/memory).
