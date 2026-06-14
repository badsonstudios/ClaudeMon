---
name: explain
description: Explain code, a file, a subsystem, or a concept in this project — how it works, why it's built that way, and how the pieces fit together. Read-only.
user-invocable: true
---

Explain the requested target clearly and at the right level of detail.

Target / question: $ARGUMENTS

## Steps

1. **Identify the target.** If `$ARGUMENTS` names a file, symbol, feature, or
   concept, focus there. If it's vague, ask one quick clarifying question or pick
   the most likely interpretation and state your assumption.
2. **Read the relevant code** and enough surrounding context to be accurate
   (callers, types, config, tests). Don't guess at behavior you can verify.
3. **Explain at the right altitude:**
   - Start with a one-paragraph plain-language summary.
   - Then how it works step by step / the data or control flow.
   - Call out *why* it's done this way when the project's conventions
     (`references/architecture.md`, `references/tech-stack.md`) explain it.
4. **Anchor to the code** with `file:line` references so the reader can jump in.

## Output

- **Summary** — what it is, in one or two sentences.
- **How it works** — the mechanics, ordered and concrete.
- **Key files** — `file:line` pointers to the important parts.
- **Gotchas / notes** — edge cases, assumptions, or things that surprise people.
- Optional: a small ASCII diagram if it clarifies the flow.

Do not modify anything — this skill only explains.
