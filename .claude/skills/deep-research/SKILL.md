---
name: deep-research
description: Comprehensive, multi-source web research on technical topics, libraries, APIs, and best practices — with cross-referenced, cited findings.
user-invocable: true
---

Conduct thorough web research and synthesize cited findings.

Topic / question: $ARGUMENTS

## Process

1. **Define scope** — restate the question and break it into sub-questions. If the
   question is underspecified, ask 2–3 clarifying questions first.
2. **Gather sources** — search for authoritative, recent material. Prefer official
   docs, reputable engineering blogs, and well-maintained repositories. For larger
   efforts, delegate to the `deep-research-agent`.
3. **Cross-reference** — verify claims across multiple independent sources; note
   where they disagree or where information may be outdated.
4. **Synthesize** — organize findings into a clear, structured report.

## Output

- **Summary** — the short answer / key takeaways.
- **Details** — findings organized by sub-topic, with code examples where useful.
- **Recommendations** — concrete next steps for this project.
- **Sources** — list of URLs consulted (always include).
