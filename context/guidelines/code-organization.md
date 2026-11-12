# Solution-wide Code Organization Guidelines

These guidelines apply across the MediaArchive solution. Follow the applicable project-level `AGENTS.md` for component-specific conventions.

- Organize code by cohesive responsibility. Prefer one public top-level type per matching filename; keep small private or nested types near their owner.
- Prefer existing project patterns and platform capabilities. Add dependencies, layers, or abstractions only when there is a demonstrated need.
- Keep refactors small and behavior-preserving. Preserve public APIs, observable behavior, ordering, diagnostics, and error handling unless a change explicitly requests otherwise.
- Keep command-line orchestration in the CLI and application/domain logic in Core.
- Add or update tests for behavior changes, and update contract documentation when public interfaces or observable behavior change.
