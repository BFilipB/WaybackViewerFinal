---
applyTo: "tests/**"
---
# Test rules
- xUnit; reuse fakes/helpers in `TestSupport.cs`. No real network calls to archive.org.
- Use fake `HttpMessageHandler` and `TimeProvider` for retry/pacing/cancellation scenarios.
- One behavior per test; name as `Method_Scenario_Expected`.
- Cover failure paths (transient, permanent, cancellation, resume after restart), not only happy paths.
- Browser (Playwright) tests are for UI flows only: pagination, workflow bars, timeline.
