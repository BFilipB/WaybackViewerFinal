---
mode: agent
description: Add xUnit tests for a class or behavior
---
Add tests for ${input:target:class or behavior}.
- Follow `.github/instructions/tests.instructions.md`.
- Cover happy path, transient failure, permanent failure, cancellation, and resume/interruption where relevant.
- Run the new tests and make sure they pass without changing production behavior (report bugs found instead).
