---
name: check-code
description: Analyze code quality and identify issues, improvements, and good practices. Focuses on cleanliness, maintainability, and conventions rather than security.
user-invocable: true
---

Run a code-quality analysis on recently changed files.

If the user specified files or scope: $ARGUMENTS

## Step 1: Identify changed files

Run `git diff HEAD --name-only` to find changed files. If there are no
uncommitted changes, use `git diff main...HEAD --name-only`.

## Step 2: Read each changed file

Read every changed file before evaluating.

## Step 3: Check for quality issues

### Cleanliness
- Unused imports / `using` statements
- Commented-out code that should be deleted
- Dead code (unreachable branches, unused functions)
- Debug/print statements left in production code
- TODO/HACK/FIXME comments that should be addressed or tracked

### Naming & conventions
- Follows the project's conventions (see `references/code-style.md`)
- Consistent naming across similar constructs
- Descriptive names (no single-letter names except loop counters)

### Organization
- Functions/methods over ~50 lines that should be broken up
- Types/classes with too many responsibilities
- Duplicated logic that should be extracted

### Correctness & error handling
- Swallowed exceptions / empty catch blocks
- Overly broad catches where specific handling is warranted
- Missing validation on public inputs
- Async correctness (missing `await`, fire-and-forget, etc.)

### Secrets check
- No credentials, tokens, or passwords hard-coded — they belong in `.env`.

## Step 4: Produce report

Group findings by category. For each issue give a `file:line` reference, what's
wrong, and a suggested fix. End with an overall rating: **Good / Needs Work /
Significant Issues**.
