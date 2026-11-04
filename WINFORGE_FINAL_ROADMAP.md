# WinForge Final Roadmap

## Phase 1: Product maturity and evidence surfacing

### Objective
Make the current intelligence features feel like a coherent, trustworthy product instead of a set of specialized engines.

### Scope
- Integrate software identity results into the primary user flow.
- Make evidence, confidence, and limitations more visible across inventory, drift, and digital-twin views.
- Improve interoperability between software identity, dependency analysis, and diagnostic findings.

### Dependencies
- Existing identity engine and Digital Twin graph models
- Current passport and digital-twin application services
- Read-only telemetry and scan infrastructure

### Acceptance criteria
- A user can understand how a product was reconciled without reading implementation code.
- Confidence and source are shown with each assessment and impact candidate.
- Missing data is labeled explicitly rather than implied.

## Phase 2: UX and historical investigation experience

### Objective
Turn the existing engines into a guided system-investigation experience.

### Scope
- Better entity detail panels and evidence lists
- More structured comparison and historical timelines
- More direct links from findings to affected relationships and impacted components
- Clear filtering and search by confidence, source, and category

### Dependencies
- Digital Twin comparison engine, Guardian findings, and inventory history
- App-level view models and WPF navigation

### Acceptance criteria
- Investigation steps are understandable to a non-expert user.
- The app differentiates evidence from inference.
- Historical change summaries are readable without requiring raw domain objects.

## Phase 3: Release hardening and validation

### Objective
Prepare the application for safe distribution outside the engineering environment.

### Scope
- Packaging, installer validation, and update testing
- Deployment smoke tests and compatibility checks
- Production-style failure handling
- Security review of logs, data retention, and dump handling

### Dependencies
- Existing solution build and test baseline
- Documentation and release notes
- Windows environment validation matrix

### Acceptance criteria
- Release build is reproducible in a clean environment.
- Upgrade and reinstall paths are validated.
- User guidance clearly defines local-first, read-only constraints.

## Phase 4: Strategic product expansion (optional)

### Objective
Only after core quality is stable, expand into advanced product workflows.

### Scope
- Deeper relation modeling across software and hardware
- Additional diagnostic scenarios and evidence integration
- Wider policy and compliance review for enterprise use

### Dependencies
- Prior phases completed and validated

### Acceptance criteria
- The core product is stable before broader scope expansion continues.

## Current priority

The single highest-value immediate direction remains product maturity: integrate evidence-rich identity and diagnostic surfaces into the app and then harden the release story around the existing safe architecture.
