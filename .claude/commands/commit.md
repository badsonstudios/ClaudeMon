---
description: Stage (if needed) and commit the current changes — asks for approval first.
---

Commit the current work. An optional commit message may follow: $ARGUMENTS

1. Show `git status` and a summary of `git diff` so the user sees what will be committed.
2. **Ask for approval before committing** (skip only if the user already said to
   commit without asking this session).
3. Stage the intended files and commit with a clear, present-tense message.
   Reference issues with `Fix #<n>:` / `Closes #<n>:` when applicable. Follow
   `.claude/skills/startup/references/git-workflow.md`.

Never stage `.env` or other secrets (a hook will block it anyway). For pushing and
opening a PR, use `/pr`.
