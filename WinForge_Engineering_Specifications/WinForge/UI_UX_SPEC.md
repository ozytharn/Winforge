# WinForge — UI and UX Specification

**Version:** 1.0  
**Status:** Initial product interface specification

---

# 1. Product Experience

WinForge should feel like a serious, polished Windows desktop utility.

The interface must be:
- Clear.
- Consistent.
- Fast.
- Accessible.
- Evidence-oriented.
- Transparent about system changes.
- Understandable to non-expert users without hiding technical details.

Avoid clutter, misleading health scores, unnecessary animations and decorative controls without functionality.

# 2. Navigation

Primary navigation:

1. Overview
2. PC Passport
3. Digital Twin
4. Processes & Services
5. Software Center
6. Storage & Cleanup
7. Performance Governor
8. Gaming & Workload
9. Drivers & Hardware
10. Security & Privacy
11. Repair & Diagnostics
12. Time Machine & Recovery
13. Automation & Guardian
14. Reports & Benchmarks
15. Activity & Decisions
16. Settings

Navigation may be grouped into logical sections, but do not hide core features behind confusing menus.

# 3. Overview Dashboard

Show:
- PC identity and OS build.
- PC Passport scan freshness and coverage.
- CPU, GPU, RAM, disk and network metrics where supported.
- Guardian mode and current permission scope.
- Important evidence-backed issues.
- Pending approvals.
- Recent system changes.
- Recovery readiness.
- WinForge's own resource usage.

Do not show fabricated metrics or a single unexplained health score.

The current Overview composes real Passport, Digital Twin, Recovery, Backup Intelligence, and Guardian state with timestamps. It surfaces current errors, active Guardian alerts, and unknown observations. Values remain unavailable when a source cannot report them; WinForge does not synthesize aggregate readiness or performance scores.

# 4. PC Passport

Provide:
- Hardware inventory.
- OS and update information.
- Device inventory.
- Driver inventory.
- Software inventory.
- Services and startup entries.
- Scan history.
- Coverage and permission information.

Every inventory page should show last verified time and whether data is current, stale, partial or inaccessible.

# 5. Digital Twin

Provide:
- Searchable component graph.
- Component detail view.
- Relationship evidence.
- Confidence.
- Direct versus inferred relationship.
- Last verified time.
- Historical changes.
- Impact-analysis entry point.

Avoid graph visualizations that become unreadable with large inventories. Provide filters, search and list-based alternatives.

The initial Digital Twin screen uses a searchable entity list and evidence/details pane with bounded dependency exploration; it does not attempt an unbounded graph visualization.

# 6. Processes and Services

Show:
- Name and publisher where known.
- Current state.
- Purpose in plain language.
- CPU/RAM/disk/network measurements with time window.
- Startup/service configuration.
- Dependencies and associations.
- Protection status.
- History.
- Why WinForge recommends an action.

Distinguish:
- Running.
- Stopped.
- Disabled.
- Deferred.
- Protected.
- Pending.
- Failed.
- Restored.

A stopped service is not necessarily disabled or unnecessary.

# 7. Recommendation Cards

Every recommendation must show:
- What was detected.
- Why it matters.
- Evidence.
- Risk.
- Confidence.
- Expected benefit.
- Potential downside.
- Dependencies.
- Reversibility.
- Required approval.
- Recovery preparation.

Do not use colour as the only way to communicate risk.

# 8. Approval Experience

Before a consequential change, show:
- Exact target.
- Exact operation.
- Current state.
- Proposed state.
- Expected impact.
- Risk and confidence separately.
- Whether a restart is required.
- Recovery available.
- Irreversible or uncertain aspects.

The user must be able to reject the action without losing access to the rest of the application.

# 9. Guardian

Display:
- Current mode.
- Active policies.
- Allowed operation scopes.
- Protected components.
- Pending approval count.
- Recent decisions.
- Reasons for denial.
- Monitoring intensity.

Changing Guardian mode must explain the permission consequences.

The initial Guardian Observer surface is not a Guardian policy editor or automation center. It is user-started, read-only telemetry with an explicit sampling interval, configurable warning/critical thresholds, source/availability states, bounded persisted history with user-configurable retention, time-range aggregation, previous-period summaries, and historical threshold-crossing counts. It distinguishes unavailable readings and sampling gaps from zero values, and displays confirmed alerts with their supporting observation/source. Do not imply that Assistant/Guardian modes, Guardian policy approvals, or remediation are implemented. The separate Action Broker tab supports explicit approval only for synthetic simulation requests and is not approved for real system-changing operations. Unsupported GPU measurements must not be shown as zero or with success indicators.

# 10. Recovery Center

Display:
- Recovery readiness.
- Backup categories.
- Last creation time.
- Integrity status.
- Restore test status.
- Storage location.
- Scope and limitations.
- Recovery instructions.

Never present an untested backup as fully verified.

The current Recovery Center includes a Recovery Backups surface with provider/state filtering, selected candidate details, bounded inspect/cancel controls, exact checks and skipped-check explanations, inspection history, and a comparison of the latest two results. Discovery remains limited to fixed candidate folders at ready mounted-volume roots. Clearly differentiate a detected folder from recognized structure and bounded metadata checks. State `Verified` applies only to checks performed; do not use reassuring success language or imply full integrity, completeness, or restoreability.

# 11. Monitoring

Charts must show:
- Units.
- Sampling interval.
- Time range.
- Missing data.
- Data source where relevant.

Do not smooth or interpolate missing samples in a way that suggests measurements exist.

# 12. Settings

Include:
- Monitoring profile.
- Startup behaviour.
- Guardian mode and policies.
- Privacy and research consent.
- Data retention.
- Backup location.
- Notifications.
- Appearance.
- Diagnostics export.
- Update channel.
- About and version.

# 13. Visual Design

Use a coherent design system:
- Consistent spacing scale.
- Consistent typography.
- Consistent icon style.
- Clear hierarchy.
- Light and dark themes.
- High-DPI support.
- Keyboard focus indicators.
- Accessible contrast.
- Predictable dialog behaviour.

Avoid excessive gradients, large decorative hero panels and unnecessary glass effects that reduce readability.

# 14. Interaction Rules

- No fake buttons.
- No silent actions.
- No hidden destructive actions.
- No unexplained toggles.
- No ambiguous labels such as "Boost" without describing the change.
- Show progress and cancellation where safe.
- Confirm destructive operations.
- Provide useful error messages.
- Preserve user context when navigating back.

# 15. Accessibility

Support:
- Keyboard navigation.
- Screen readers through accessible names.
- High contrast where feasible.
- Text scaling.
- Non-colour status indicators.
- Clear focus states.

# 16. UI Acceptance Criteria

A page is complete only when:
- It displays real data.
- Loading, empty, partial and error states exist.
- Controls perform their advertised actions.
- Dangerous actions are approval-gated.
- Keyboard navigation works.
- Data freshness is visible.
- No placeholder metrics remain.
