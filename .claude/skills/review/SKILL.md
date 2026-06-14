---
name: review
description: Deeper review of changed code for architecture, correctness, and best practices. Delegates to the code-reviewer agent for an isolated, read-only pass.
user-invocable: true
---

Review the current changes for architecture, correctness, and best practices.

If the user specified files or scope: $ARGUMENTS

## Step 1: Determine scope

Run `git diff HEAD --name-only` (or `git diff main...HEAD --name-only` if there
are no uncommitted changes) to find what changed.

## Step 2: Delegate to the code-reviewer agent

Launch the `code-reviewer` agent (read-only) with the list of changed files. Ask
it to assess:

- **Architecture** — does the change fit the project's structure and patterns
  (see `references/architecture.md`)?
- **Correctness** — logic errors, edge cases, race conditions, async issues.
- **Best practices** — idiomatic use of the language/framework in
  `references/tech-stack.md`.
- **Maintainability** — clarity, coupling, testability.
- **Security smells** — anything that warrants a deeper `/security-review`
  (e.g. unvalidated input, hard-coded secrets).

## Step 3: Present findings

Summarize the agent's findings grouped by severity (Blocker / Should-fix /
Nit), each with a `file:line` reference and a concrete recommendation. Note any
follow-up skills worth running (`/check-code`, tests, etc.).
