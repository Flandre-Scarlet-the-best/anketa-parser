# DSH

When the filesystem skill provider is mounted with default roots, it discovers
`.agents/skills` at the project root; configuration may change that. Confirm the
session catalog and actual worktree. The official direct `lsp` tool requires a
configured provider and workspace and supports `goToDefinition`, `findReferences`,
`goToImplementation` and `hover`. Positions are one-based UTF-16; responses can
be capped. A cwd change affects later queries, but does not change an already
running server root. Verify server state and result paths.

These four operations do not provide diagnostics, rename or code actions. Use a
separately verified plugin or the common compiler/analyzer fallback for those
capabilities. Report unavailable operations and partial results. See the official
[filesystem provider](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/skill/skill-filesystem/README.md)
and [LSP tool](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/lsp/tool-lsp/README.md).
