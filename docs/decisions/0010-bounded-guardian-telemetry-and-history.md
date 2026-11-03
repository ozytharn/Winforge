# ADR 0010: Bounded Guardian telemetry and historical analysis

**Status:** Accepted

## Context

System Intelligence needs useful historical resource observations without silently starting continuous monitoring, causing unbounded local database growth, or presenting provider gaps as healthy zero values. Network counters are cumulative, process identifiers can be reused, and a generic WMI GPU provider does not reliably expose live sensor values.

## Decision

- Keep Guardian sampling explicitly user-started, asynchronous, cancellable, and read-only; do not start collection automatically.
- Persist timestamped telemetry snapshots through the existing SQLite repository using additive migration 5. Keep migrations 1–4 and their checksums unchanged.
- Bound each serialized record to 64 KiB, time-range requests to 10,000 rows, retention to 1–365 days and 100–100,000 samples, and UI summaries to a bounded result set. Insert and retention pruning occur in one transaction.
- Store explicit source, availability, limitation, and sample interval evidence. Persistence failures remain visible while the current live observation can still be displayed.
- Derive network throughput from successive interface byte-counter observations and elapsed monotonic time. Represent first baselines, resets, disconnected interfaces, and failed reads as unavailable.
- Bound process enumeration and retained observations. Use PID plus start-time ticks as identity where accessible, report CPU from per-process time deltas, and do not collect command lines or modify processes.
- Use `Win32_VideoController` only for adapter identity. Report utilization, dedicated memory, and temperature as Unsupported until a reliable, reviewed provider exists.
- Require threshold findings to persist across two consecutive samples before surfacing them as active alerts; suppress duplicate transitions and resolve alerts when evidence clears or becomes unknown.
- Summarize historical warning and critical threshold crossings separately from current active alerts, preserving the threshold configuration used for the summary.
- Compare Digital Twin entities and relationships with evidence, provenance, confidence, and observation time. Classify absence as `NotObserved` unless three chronologically ordered snapshots have distinct inventory scan watermarks and the same non-empty scope; even then use `PotentiallyRemoved`, not a definitive removal claim.
- Keep the Overview dashboard a composition of actual collected or persisted state, warnings, and timestamps; do not compute an opaque health score or forecast failure.

## Consequences

- Guardian users can inspect bounded historical summaries across restarts without collection running in the background.
- SQLite migration 5 adds one indexed telemetry table while keeping large raw inventories out of telemetry history.
- Portable live GPU sensor values, ETW, service/device-state monitoring, predictive resource exhaustion analysis, and automated corrective actions remain unsupported.
- A passing sample or threshold assessment is not a guarantee of system health, future capacity, or hardware reliability.
- Same-scope repeated Digital Twin absence is a cautious hint only; incomplete or partial scans can still produce false absence, so the UI must preserve the evidence limitation.
