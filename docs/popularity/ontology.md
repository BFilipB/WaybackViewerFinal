# Ontology

| Object | Meaning | Key fields |
|---|---|---|
| **Site** | A host (`forum.example`) | host |
| **PageTemplate** *(planned)* | URL shape within a site (`/thread/{id}`, `/user/{name}`) | site, pattern |
| **Page** | One original URL | url, site, template |
| **Capture** | One Wayback snapshot of a page | timestamp, mime, status |
| **Evidence / Signal** | One observation about a page | kind, value, source, confidence |
| **PopularityEstimate** | Combined result shown to users | score (EngagementScore), confidence |

Rules:
- Evidence is never compared across sources by raw value; it is normalized (percentile) within a site, later within a template.
- Every estimate carries a confidence: 0.6+ real counts, 0.2 page-content estimate, 0.1 archive-only signal.
