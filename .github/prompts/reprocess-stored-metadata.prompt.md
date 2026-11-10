---
mode: agent
description: Make stored popularity metadata refresh after extractor changes.
---
Run as `reprocessor`. Add `ExtractorVersion`, the schema/migration change, the stale-row check in `PageService`,
and the resumable bounded background pass with tests. Build and run all tests. Log in `docs/popularity/experiments.md`.
Never delete user data; ask before any destructive step.
Follow the Policy section in docs/popularity/GUIDE.md: unknown is null, estimates excluded from exact filters by default, fixture first, bump ExtractorVersion on extractor changes, report coverage.
