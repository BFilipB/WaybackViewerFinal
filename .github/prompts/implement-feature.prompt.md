---
mode: agent
description: Implement a feature end-to-end following WaybackViewer conventions
---
Implement: ${input:feature:Describe the feature}

1. Read `.github/copilot-instructions.md` and the relevant scoped instructions.
2. Locate affected code (services, storage, controller, view) and list related improvements briefly before starting.
3. Make a short plan if more than one area is touched.
4. Implement with minimal changes, cancellation propagation, no swallowed errors.
5. Add/update xUnit tests.
6. Build and run related tests; fix failures.
7. Summarize changes and follow-up suggestions.
