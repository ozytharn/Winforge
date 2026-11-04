# WinForge Technical Debt

## Current debt

- Product maturity is strong but still not polished enough for a broad public release.
- Identity reconciliation is implemented, but product-surface integration needs to keep improving as more evidence types are added.
- UI flows still feel like engineering views in some sections rather than a fully guided investigation experience.
- Release packaging, installer validation, and upgrade path testing remain incomplete.
- Advanced diagnosis and dependency analysis are not yet fully expressed as a cohesive public-facing workflow.
- Action Broker host-specific consent, identity, and anti-rollback architecture remain explicitly deferred.

## Why it matters

These are not code defects so much as productization gaps. The application is now substantially complete as a local-first engineering platform, but the remaining work is about making that platform reliable, understandable, and production-ready for wider adoption.

## Risk treatment

- Keep the current implementation read-only and evidence-first.
- Do not widen the trusted execution boundary until architecture and security reviews support it.
- Prefer product clarity over speculative feature expansion.
- Treat release packaging and upgrade validation as first-class engineering work, not finish-line decoration.
