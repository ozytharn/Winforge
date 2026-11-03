# ADR 0006: Persistent evidence-based Digital Twin

**Status:** Accepted

## Context

PC Passport already stores timestamped observations, source provenance, and explicit field availability. The Digital Twin needs to relate that evidence without introducing duplicate collectors or implying relationships that the inventory cannot support.

## Decision

- Build snapshots from existing PC Passport observations through the Application layer.
- Give each entity a type-qualified stable identity and each relationship its source, evidence, confidence, direction, and observation time.
- Keep evidence-backed association distinct from dependency; do not infer relationships from display-name similarity.
- Represent missing data as `NotObserved`, never as confirmed removal.
- Bound graph traversal by depth and node count.
- Persist snapshots additively in SQLite migration 3, with transactional writes and checksum/count/endpoint verification on reads.
- Keep migration 1 and 2 definitions and checksums immutable.

## Consequences

- Existing inventory collection and observation history remain the source of truth for observed system facts.
- Digital Twin snapshots can be browsed and compared without deleting old observations.
- Inferred and direct relationships remain distinguishable, but current rules cover only supported inventory metadata and are not a complete dependency graph.
- Historical snapshots increase database size; retention policy remains future work.
