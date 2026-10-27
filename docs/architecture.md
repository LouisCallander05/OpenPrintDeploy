# Architecture and security

This document describes **release 0.10.3**. Use the
[release tag](https://github.com/LouisCallander05/OpenPrintDeploy/tree/v0.10.3)
when comparing implementation; the default branch may differ. The
[README](../README.md) and [operations runbook](operations.md) are the technician
entry points. No private planning vault is required to operate the release.

## Components and identities

| Component | Identity and responsibility |
| --- | --- |
| Server | ASP.NET Core 8 Windows service, normally Local SYSTEM; web admin, sync API, SQLite, directory queries and local driver export |
| Tray | Ordinary signed-in user; authentication, assignment sync and per-user printer connections |
| Driver service | Local SYSTEM; restricted machine-configured driver preparation over a local named pipe |
| Cleanup helper | Privileged setup of protected cleanup requests, then printer removal in each user's context |

The tray resolves assignments as the user. The driver service does not resolve
groups or accept the user's password. Print jobs still use Windows printing;
they are not sent through the OPD HTTPS API.

## Assignment flow

1. The tray authenticates `POST /sync` using Windows Negotiate.
2. The server prefers group SIDs from the authenticated token. If there are none,
   it falls back to directory resolution.
3. Zones match stored group SIDs using OR semantics; matching zone printer sets
   are combined. Priority is a display order only.
4. The response includes desired printers, whether resolution is authoritative,
   explicit global removals and a sync correlation ID. On HTTPS, authoritative
   assignments include protected driver tickets for new clients.
5. The client compares assignments with installed connections and its persisted
   managed state. A failed HTTP request does not produce a removal plan. A
   non-authoritative response preserves zone assignments, but still carries
   explicit global removals.
6. New clients prepare drivers for assigned queues, including existing
   connections so updates can be detected, before applying connection changes.
7. Best-effort `POST /sync/report` records outcomes. Report failure does not undo
   completed printer operations or turn them into a new reconciliation pass.

The client can adopt an already-present assigned connection. Adoption avoids
re-adding it, but puts it under future zone-removal management. Uninstall retains
adopted connections by default while cleaning up OPD-created ones. Explicit global
removal is independent of this ownership distinction.

An authoritative empty result means no assignments, not an outage. Wrong zone
configuration or a successful lookup of the wrong account can therefore have
real removal effects. No protocol can infer the operator's intended membership
from an otherwise valid empty result.

0.9.17 and 0.10.3 sync contracts are compatible; 0.9.17 ignores the added driver
ticket field. This does not install the privileged service on old clients or
validate every Windows policy/driver combination.

## Authentication and administration

Production admin login defaults to an HTTPS form. Windows `LogonUser` validates
credentials; an encrypted HttpOnly/Secure cookie references a server-side,
expiring ticket store. Passwords are not stored in admin sessions or logs.
Sessions last at most eight hours and are lost on service restart or sign-out.
Group claims are a sign-in-time snapshot. Permission checks and circuit
revalidation limit continued access after admin grant changes.

Login/logout POSTs use antiforgery protection. Login attempts have per-account
throttling and an IP rate limit. Native Windows logon is bounded to four concurrent
workers and a 20-second response wait; timed-out native calls hold their worker
until Windows returns.

`Auth:AdminScheme=Negotiate` retains browser Windows SSO. Legacy `Basic` admin
configuration selects forms. **Client sync always uses its own Windows
authentication scheme**, not the browser's cookie or login form.

Admin grants in protected `admin-access.json` are combined with `Auth:Admin`
configuration grants. Explicit users match full `DOMAIN\user` names or primary
user SIDs; a bare username is not a cross-domain alias. Prefer group SIDs where
group names may be ambiguous. With no grants, production bootstrap requires the
local Administrators SID. An unreadable grant store fails closed; configured
emergency grants are the recovery route.

Standalone clients can store explicit domain credentials in the current user's
Windows Credential Manager, keyed by the OPD host. Domain/Entra-joined clients
attempt integrated authentication; their ability to authenticate to on-premises
services depends on the Windows/domain deployment. OPD credentials do not create
permissions to Windows print shares.

Development header authentication is registered only in Development. The server
refuses to run as a Windows service in that environment.

## Directory transport and caching

Default LDAP configuration is Negotiate over LDAPS/636. With Local SYSTEM on a
domain-joined server, the process uses the computer account. Blank server and
search-base values trigger domain discovery. Explicit domain-qualified lookups
are routed to that domain and fail closed if it cannot be resolved; unqualified
names belong to the home domain. Group-name lookup requires a unique result.

The DC certificate must be trusted by the OPD machine. Certificate-validation
bypass is rejected. Basic binds require TLS. An intentionally configured
non-SSL Negotiate connection requests signing and sealing.

Friendly group-name/SID lookups are cached for ten minutes and the group catalogue
for sixty seconds. Zone editing uses already-loaded zone data and resolves labels
in the background. Caching labels is distinct from refreshing a user's Windows
authentication token after a group-membership change.

## HTTPS and certificate lifecycle

Production requires enabled and enforced HTTPS with a usable certificate. Default
port is 5443 and no HTTP listener is configured. Provisioning uses `Https:PfxPath`
when supplied; otherwise Windows creates/reuses a self-signed certificate in the
Local Computer Personal store. Startup fails if production TLS cannot be enforced.
Some 0.10.3 comments/log messages still mention HTTP fallback; they do not override
the production startup check.

Clients use normal certificate validation or an exact configured thumbprint pin.
The generated client filename carries a host and optional pin; an explicit URL is
needed for non-default ports. HTTP clients refuse redirects and scope credentials
to the configured server's Negotiate authentication.

The self-signed lifetime defaults to 100 years (configurable, clamped to 1–100).
That implementation default is not an organisation's certificate lifecycle policy.
A certificate can still be replaced, lost or approach expiry, so retain recovery
material and monitor expiry. Rotation always changes its thumbprint.

For planned self-signed rotation, record existing settings and a recovery point,
use **Settings → Connection security → Regenerate certificate**, restart the
service in the maintenance window, then distribute the new pin/trust and verify
clients. Coordinate the interruption; old pins will not trust the replacement.
For CA-issued certificates without pins, renewals rely on continued valid chain
and hostname verification. A configured pin still requires updating even for a
CA-issued certificate.

## Driver preparation and trust boundaries

HTTPS sync issues short-lived, protected tickets scoped to registered assigned
queues. The SYSTEM service accepts a queue and ticket over a local pipe; it
does not accept arbitrary URLs, commands, file paths or user credentials. Its
configuration comes from protected machine settings, not user environment values.

The service checks an exact hostname allow-list, downloads only from its configured
HTTPS origin, validates lengths/hashes and archive paths, and lets Windows validate
the complete signed package. The server exports locally installed Driver Store
packages. The client installs the package and verifies the driver identity before
the user connection is attempted without interactive installation prompts.

The protected first pipe instance remains reserved between requests. In 0.10.3,
response handling waits for the client to read and close, with a deadline, before
reuse. This fixes responses being discarded by an early disconnect. A prepared
connection failing with error 3012 gets one retry through standard Windows driver
selection, still without UI; it is not a general Windows printer repair.

Signed drivers can still be faulty or vulnerable. A trusted server compromise can
affect the fleet. Publisher approval remains an administrative responsibility;
OPD neither trusts new publishers automatically nor relaxes Windows driver policy.
Tickets are reusable bearer capabilities until expiry, not single-use tokens.
See [driver delivery](driver-delivery.md) for limits, timeouts and pilot requirements.

## Storage and recovery

SQLite via EF Core stores printers, zones/rules, explicit removals and client
activity under `%ProgramData%\OpenPrintDeploy\app.db` by default. Admin grants
are a separate protected JSON file. Driver ticket keys are machine-protected under
`%ProgramData%\OpenPrintDeploy-DriverKeys`. Driver package caches are separate.

SQLite uses WAL, `synchronous=NORMAL` and a five-second busy timeout. Migrations
run on startup. Existing database/WAL/SHM files receive best-effort timestamped
copies before pending migrations; failure logs a warning and migration continues.
This is not a verified or independent backup. The current implementation uses
SQLite; changing a connection string alone does **not** add SQL Server support.

Protected storage rejects unsafe owners, write grants and reparse paths. The
0.10.2+ MSI migration handles specific legacy directory permissions before service
startup; it does not permit arbitrary writable storage. Ordinary users should
not be granted write access to recover a failed startup.

The client persists per-user management/provenance state. Uninstall stages
protected machine requests for per-user cleanup and leaves installed drivers
because other queues may depend on them. Uninstall cleanup is skipped during
major upgrades. See the [operations runbook](operations.md#backup-and-recovery)
for rollback limits and a backup inventory.

## Scope limits

There is no automatic driver rollback, independent driver approval ring, or
dedicated externally managed drivers mode in the 0.10.3 client. Pausing driver
updates does not stop normal assignment removal. Remote-server driver export,
ARM64/x86 delivery, vendor EXE installers, macOS/Linux clients, cost accounting
and pull printing are outside this implementation's supported scope.
