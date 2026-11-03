# ADR 0008: User-started passive Guardian Observer

**Status:** Accepted

## Context

WinForge needs a safe first monitoring increment before implementing Guardian policies or system-changing actions. The desktop app should not silently begin persistent or high-frequency monitoring.

## Decision

- Require an explicit user action to start monitoring and provide a cancellation/stop path.
- Collect only system-wide CPU, physical memory, fixed mounted-volume capacity, and uptime in this increment.
- Use documented read-only Windows APIs/managed interfaces and expose source, availability, and limitations.
- Calculate CPU from system-time deltas; the first sample establishes a baseline and is not reported as a numeric measurement.
- Use configurable warning thresholds but never autonomously act on them.
- Sample every two seconds while active and retain at most 60 samples in memory; do not persist telemetry yet.
- Keep Guardian policies, Assistant/Guardian modes, approvals, ETW, and Action Broker integration out of scope.

## Consequences

- The Observer gives immediate local telemetry without elevation or system changes.
- GPU, network, per-process, device/service-state, temperature, and durable history are not available in this increment.
- In-memory history is lost when the application closes; durable collection requires privacy, retention, and database decisions.
