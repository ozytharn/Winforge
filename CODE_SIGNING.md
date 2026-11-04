# WinForge Code Signing Readiness

## Goal

Prepare the project for legitimate Windows code signing without committing private keys or credentials to the repository.

## Readiness requirements

- Windows executables and installer payloads should be signed by a trusted certificate.
- Signing credentials must be supplied externally at release time.
- Private keys must never be committed to source control or build scripts.
- The build must fail clearly if signing is requested without credentials.
- After signing, the signature should be verified before the artifact is promoted.

## Internal fallback

When signing credentials are unavailable, the release pipeline can produce an unsigned internal artifact for review, but it must remain clearly labeled as unsigned and not presented as signed.
