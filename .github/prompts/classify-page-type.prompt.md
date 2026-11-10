---
mode: agent
description: Add or refine page-type classification (post, listing, search, profile, home, asset).
---
Run as `page-classifier`. Start from the user's example URLs (for example a `/search/` page or a subreddit root ranking next to posts).
Add the failing tests first, implement the pure classifier, wire it into count-specific sorts, run the popularity tests and build,
update `docs/popularity/ontology.md` and `experiments.md`. Ask before any change that reorders existing rankings.
