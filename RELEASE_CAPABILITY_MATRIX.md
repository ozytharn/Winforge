# WinForge Capability Matrix

This matrix reflects the current source state in the checked-out repository after validation and release packaging hardening. Statuses are intentionally conservative: "Implemented" means the feature is in the codebase and integrated; "Tested" means coverage exists in project tests; "UI" means user-visible in the WPF app; "Documented" means the behavior is described in the repo docs.

| Capability | Implemented | Integrated | Tested | In UI | Documented | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| PC Passport and hardware inventory | Yes | Yes | Yes | Yes | Yes | Reads local Windows inventory and stores evidence in SQLite. |
| Software inventory and identity | Yes | Yes | Yes | Partial | Yes | Inventory and reconciliation are implemented; UI presentation is oriented to overview and findings. |
| Driver inventory and driver health | Yes | Yes | Yes | Yes | Yes | Driver health and unsigned-driver scenarios are covered. |
| Storage health | Yes | Yes | Yes | Yes | Yes | Storage and volume health are aggregated into system health. |
| Security and privacy intelligence | Yes | Yes | Yes | Yes | Yes | Read-only, evidence-based assessment for Defender, firewall, update, BitLocker, UAC, Secure Boot, startup, and privacy config. |
| Privacy configuration assessment | Yes | Yes | Yes | Partial | Yes | Conservative registry-backed evaluation with unknown/unavailable handling. |
| Guardian telemetry | Yes | Yes | Yes | Yes | Yes | Local read-only observations with bounded retention. |
| Digital Twin and comparisons | Yes | Yes | Yes | Yes | Yes | Evidence-based graphs and bounded impact analysis remain read-only. |
| Configuration drift detection | Yes | Yes | Yes | Partial | Yes | Implemented on inventory snapshots, with conservative handling of missing data. |
| Root-cause investigation | Yes | Yes | Yes | Partial | Yes | Read-only correlation only; no causation claims beyond evidence. |
| Recovery readiness | Yes | Yes | Yes | Yes | Yes | Recovery and backup inspection remain read-only and bounded. |
| Backup intelligence | Yes | Yes | Yes | Yes | Yes | Inspection focuses on structure and metadata, not destructive recovery. |
| Action Broker V1 simulation | Yes | Yes | Yes | Yes | Yes | Simulation-only, not approved for real Windows changes. |
| Action Broker V2 hostless contracts | Yes | Yes | Yes | Partial | Yes | Hostless architecture exists and is documented. |
| Release packaging | Yes | Yes | Partial | N/A | Yes | The publish script is deterministic and creates a manifest; installer-level validation remains out of scope. |
| First-run database initialization | Yes | Yes | Yes | Yes | Yes | Local SQLite database initializes safely on first run. |
| Migration safety | Yes | Yes | Yes | N/A | Yes | Existing schema migration tests cover legacy upgrade behavior. |
| Clean-machine deployment validation | Partial | N/A | Partial | N/A | Partial | Local validation is complete; clean Windows public-target validation is not yet independently demonstrated. |

## Current status summary

- Implemented and integrated: Yes
- Tested across full solution: Yes
- Release package generation: Yes
- Public installer or external distribution proven: No
- Final public release verdict: Internal release candidate only
