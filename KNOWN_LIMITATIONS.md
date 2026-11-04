# WinForge Known Limitations

## Product maturity

WinForge is currently a strong internal release candidate, not a polished public consumer installer. The project is functionally coherent and vetted in the repository environment, but several release-readiness gaps remain.

## Non-implemented or not independently verified areas

- Full public installer workflow with signed MSI or setup package
- Upgrade validation on clean target machines
- Code signing and trusted publisher verification for external distribution
- Large external test matrix across Windows variants and hardware profiles
- Real ETW/low-level live sensor coverage for GPU or service health
- Real privileged Action Broker automation
- Predictive or autonomous remediation workflows
- Broad enterprise packaging, maintenance, and rollback testing

## Evidence quality constraints

- Some Windows providers are incomplete, OEM-specific, or vendor-dependent.
- Some metadata is unavailable without hardware-specific drivers or admin access.
- Empty or missing observations are shown as unknown or unavailable rather than healthy.
- The app intentionally avoids claims that require privileged access or evidence beyond what was observed.

## Recommended next release step

The next high-value milestone is a controlled internal rollout with clean-machine validation, packaging review, and user documentation refinement before any broad external distribution claim is made.
