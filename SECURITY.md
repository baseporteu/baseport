# Security Policy

## Reporting a Vulnerability

Report vulnerabilities through [GitHub Private Vulnerability Reporting](https://github.com/baseporteu/baseport/security/advisories/new) (**Security** tab > **Report a vulnerability**). Do not use public issues or pull requests to report security flaws.

Please include the following details in your report:

- **Environment:** Affected version (`baseport version`) and operating system/platform.
- **Component:** Affected area or endpoint (for example, `/api/v1/{apiName}/records`, admin console, wire listener, or CLI).
- **Reproduction:** Clear steps or a proof of concept (PoC).
- **Impact:** Description of what an attacker can gain and the required role or access level.

## Supported Versions

| Version | Supported |
| --- | --- |
| Latest release | Yes |
| Older releases | No |

Prior to version 1.0, fixes ship only in new releases. Older releases do not receive backports.

## Response Timelines

| Stage | Target |
| --- | --- |
| Initial acknowledgement | Within 5 business days |
| Fix or mitigation plan (High or Critical) | Within 30 days |
| Fix or mitigation plan (Medium or Low) | Next planned release |

Severity is evaluated using CVSS 3.1. Fixes are published alongside a GitHub Security Advisory crediting the reporter, unless anonymity is requested.

## Scope

### In Scope
- Source code in this repository
- Release binaries and installation scripts published from this repository
- The .NET SDK package (`Source/Baseport.Client`)

### Out of Scope
- Third-party or unowned instances
- Denial-of-service attacks relying solely on traffic volume
- Findings that depend on explicitly overriding documented safeguards (`Baseport:AllowInsecureSignIn`, `Baseport:WireRemoteAccess`, or **Allow private targets** in Settings)
- Vulnerabilities in upstream dependencies without a demonstrated exploit path through Baseport (please report these upstream)

Security testing must be strictly limited to instances that you own and operate yourself.

## Hardening

For production deployment guidance—including TLS configuration, forwarded headers, isolating the admin listener, and backup strategies—refer to [Going to production](https://baseporteu.github.io/baseport/docs/going-to-production).