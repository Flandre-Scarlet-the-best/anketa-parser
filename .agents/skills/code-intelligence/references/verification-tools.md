# Verification selection

Use the repository verification map at `.codex/context/03-VERIFICATION.md`
and its linked owners for actual commands and policies. Apply quick diagnostics
and applicable local linters while editing; use focused build and tests for
behavior and dependency changes. Select security tools for relevant changes or
rely on their configured CI coverage. CI owns versions and automated checks;
do not copy its matrix here or run every heavyweight scanner after every edit.
A passing check covers only its inputs and assertions, not all defects. Report
actual local and hosted results, failures, skipped checks and their limits.
