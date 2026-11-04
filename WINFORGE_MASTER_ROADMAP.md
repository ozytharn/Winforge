# WinForge Master Roadmap

## Executive status

WinForge is a Windows 11 local-first system intelligence application. The current baseline includes read-only inventory, recovery inspection, digital-twin evidence tracking, guardian monitoring, and a simulation-only Action Broker design boundary. The project is not a prototype only; it is a working desktop application with real Windows inventory collection and real SQLite persistence.

## Completed and verified

- Application shell and project structure.
- Read-only Windows inventory collection via WMI, SetupAPI, Configuration Manager, and SQLite persistence.
- Recovery readiness inspection and metadata analysis.
- Digital-twin model and evidence-based comparison.
- Guarded monitoring and alert assessment.
- Action Broker V2 hostless contract foundation with canonical binding and lifecycle logic.
- Identity engine for software product grouping and evidence-aware reconciliation.
- Configuration drift intelligence for successive inventory snapshots and evidence-ranked change detection.
- Read-only root-cause investigation engine that correlates telemetry, drift, and inventory evidence without asserting causation.
- Bounded Digital Twin impact analysis that rates direct and indirect relationships as informational impact signals rather than proven causal outcomes.

## In progress or active

- Identity engine consolidation into the broader product intelligence surface.
- Driver and software DNA expansion.
- Stronger research and evidence correlation.
- Guarding and Action Broker governance integration.
- Expand the configuration drift and root-cause engines with richer historical and dependency-based evidence.

## Deferred or explicitly prohibited

- Service/IPC/elevation implementation.
- Privileged system mutation paths.
- Real operation execution beyond simulation.
- Named-pipe service design activation.

## Next safe milestones

1. Consolidate software identity evidence across installed applications, drivers, processes, and startup entries.
2. Expand Product DNA and software relationship views.
3. Strengthen recovery and monitoring evidence correlation.
4. Extend Digital Twin impact assessment to higher-order relationship neighborhoods while preserving confidence and scope caps.
5. Keep Action Broker V2 in hostless contract mode until a separate architecture review approves a trusted host boundary.

## Acceptance gates

Every milestone must: preserve local-read-only behavior, remain evidence-backed, avoid fake system claims, and maintain a working compile/test baseline.
