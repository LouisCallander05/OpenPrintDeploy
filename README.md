# OpenPrintDeploy

OpenPrintDeploy assigns Windows shared printers to users through Active
Directory groups. A web console on the print server manages printers and zones;
a tray application in each user's session keeps their printer connections in
sync. The 0.10 client also includes a privileged service for driver preparation.

**This guide describes the published 0.10.3 release.** Release source is available
at [tag v0.10.3](https://github.com/LouisCallander05/OpenPrintDeploy/tree/v0.10.3);
the default branch may differ. Download installers from
[GitHub Releases](https://github.com/LouisCallander05/OpenPrintDeploy/releases).
Use a tested release for deployment, not an unversioned local build.

## Technician quick links

- [Assignments and removal](#how-printer-assignments-and-removal-work)
- [Requirements](#requirements-and-network-access)
- [Server installation](#install-and-configure-the-server)
- [Client deployment](#deploy-the-client)
- [Driver management](#driver-management)
- [Upgrades and compatibility](#upgrade-and-version-compatibility)
- [Backups and recovery](#backups-and-recovery)
- [Troubleshooting and diagnostics](docs/operations.md#troubleshooting)
- [Release acceptance checklist](docs/operations.md#release-acceptance-checklist)
- [Architecture and security](docs/architecture.md)
- [Driver delivery details](docs/driver-delivery.md)
- [Build and develop](#build-and-develop)

## How printer assignments and removal work

1. An administrator imports shared queues and adds them to zones.
2. Each zone has AD group rules stored by group SID. Matching any rule qualifies
   the user for that zone; printers from all matching zones are combined.
3. The tray authenticates as the user and syncs at startup, every five minutes
   by default, and when the user selects **Sync now**.
4. The client installs missing assigned connections, tracks existing assigned
   connections, and removes managed connections that are no longer assigned.

**Assignments can both add and remove printers.** Deleting a zone, removing a
printer from a zone, or changing group membership can therefore remove
connections on the next successful sync. Zone priority controls display order,
not which matching zone wins.

| Situation | Behaviour in 0.9.17 / 0.10.3 clients |
| --- | --- |
| Assigned printer already exists | Adopt it into OPD management without reinstalling solely for adoption |
| Managed printer is no longer assigned | Remove the user's connection, including a previously adopted connection |
| Unrelated printer was never managed by OPD | Leave it alone during normal assignment reconciliation |
| Administrator adds a UNC under **Printers → Remove printers** | Explicitly remove that connection on clients, even if another tool installed it |
| Server request fails | Keep current connections and retry later |
| Server reports directory resolution unavailable | Preserve zone assignments; explicit global removals can still apply |
| Server successfully reports an empty assignment | Remove previously managed connections that are no longer assigned |

The outage safeguard cannot distinguish a genuinely empty assignment from a
misconfigured but otherwise successful lookup. Check zone rules, user identity
and global removals before allowing a changed server to sync with the fleet.

These operations affect laptop/user connections. They do not delete shared
Windows queues on the print server. Server upgrades are intended to keep the
OPD database and assignments; backup and pilot verification are still needed.

## Requirements and network access

- A Windows x64 print server with working shared queues and installed vendor
  drivers. The standard installer runs OPD as Local SYSTEM.
- Active Directory connectivity and working DNS, time synchronisation and
  Windows authentication. Entra join alone does not establish on-premises SSO;
  validate the organisation's authentication configuration.
- Windows x64 clients with Print Spooler running and permission to use the
  queues. Install the MSI with administrator/device-management privileges;
  run the tray as the ordinary signed-in user.
- Trusted HTTPS to OPD. LDAPS is the default directory transport.
- No separate .NET runtime is needed for the self-contained release MSIs.

| Connection | Requirement |
| --- | --- |
| Browser/client → OPD server | HTTPS TCP **5443** by default |
| OPD server → domain controller | LDAPS TCP **636** by default, plus normal domain services |
| Client → Windows shared printer | Windows printing/SMB/RPC access required by domain policies |
| Print server → physical printer | Existing queue's transport, unchanged by OPD |

Opening 5443 alone does not enable Windows shared printing. Depending on print
transport, SMB 445 and RPC endpoint mapper 135 plus configured RPC ports may be
needed. Scope firewall rules to the appropriate machines; consult
[Microsoft's Windows printing RPC guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/printing/windows-11-rpc-connection-updates-for-print).

The MSI opens 5443 on the server. A different HTTPS port requires corresponding
firewall and client URL changes. Automatic driver export currently requires OPD
to run on the Windows print server hosting the queue; allowing a remote server
hostname does not enable remote driver export.

## Install and configure the server

1. Download **OpenPrintDeploy.Server.msi** from the intended release. Where
   provided, compare `Get-FileHash -Algorithm SHA256` with `SHA256SUMS.txt`.
   The 0.10.3 release MSIs are unsigned; Windows driver package signatures are a
   separate requirement.
2. Install as an administrator. For a logged unattended installation, run from
   an elevated Command Prompt in the download directory:

   ```cmd
   msiexec /i "OpenPrintDeploy.Server.msi" /qn /norestart /L*v "%TEMP%\opd-server-install.log"
   ```

3. Confirm **OpenPrintDeployServer** is running. Open the installed
   **OpenPrintDeploy Admin** shortcut or `https://<server-fqdn>:5443/`.
4. Establish browser certificate trust and sign in using a domain account.
   Without existing admin grants, first-run setup requires membership of the
   server's local Administrators group. Select the AD group allowed to administer
   OPD and retain a qualified emergency admin grant.
5. On **Directory**, run **Test connection**. Check access as the intended user
   and, where appropriate, look up `DOMAIN\user` to avoid domain ambiguity.
6. Import queues on **Printers**, create a pilot **Zone**, select its AD groups
   and printers, and save. Test with a standard user before broad assignment.

Admin sign-in uses an HTTPS login form by default. Clients continue using Windows
Negotiate authentication independently of the browser session. Admin sessions
expire after up to eight hours and end when the service restarts.

### HTTPS and directory certificates

OPD uses an administrator-supplied PFX when configured, otherwise it creates a
self-signed certificate in the local machine store. Use a trusted CA certificate
or securely distribute trust/pinning for the self-signed one. **Settings →
Connection security** shows certificate details. Do not regenerate the certificate
during an ordinary upgrade: changing its thumbprint requires pinned clients to
be reconfigured.

| Certificate | Purpose | Where trust is needed |
| --- | --- | --- |
| OPD HTTPS certificate | Browser/client connection to OPD | Admin workstations and clients, or configured client pin |
| DC LDAPS certificate | Directory queries from OPD | OPD server trusts the issuing CA and DC hostname |
| Driver signing certificate | Windows driver validation | Client machine's signing/publisher trust policy |

A working OPD HTTPS certificate does not enable LDAPS or approve a driver
publisher. See the [LDAPS runbook](docs/operations.md#directory-and-ldaps).

### Server configuration

Default configuration: `C:\Program Files\OpenPrintDeploy\appsettings.json`.
Protect it as administrator-only writable. Back it up before upgrades and compare
custom settings afterward; do not rely on MSI file replacement rules to preserve
every local edit. Restart **OpenPrintDeployServer** after configuration changes.

| Setting | Purpose / default |
| --- | --- |
| `Https:Enabled`, `Https:RequireHttps` | Must both remain enabled in production |
| `Https:HttpsPort` | `5443` |
| `Https:PfxPath`, `Https:PfxPassword` | Optional operator certificate; protect the PFX/private key and password |
| `Directory:Provider` | `Ldap` in production |
| `Directory:Ldap:Server`, `SearchBase` | Blank uses domain discovery; override for a specific DC/search base |
| `Directory:Ldap:AuthMode` | `Negotiate`, using the service identity |
| `Directory:Ldap:UseSsl`, `Port` | `true`, `636` |
| `Directory:Ldap:AllowInvalidCertificate` | Must remain `false`; bypass is rejected |
| `Auth:AdminScheme` | `Forms`; `Negotiate` optionally retains browser Windows SSO |
| `Auth:Admin:Users`, `GroupSids`, `Groups` | Emergency grants supplement Settings-page grants; users require `DOMAIN\user` or SID |
| `ConnectionStrings:AppDb` | Optional SQLite path override; default under ProgramData |
| `ClientActivity:RetentionDays`, `OnlineWindowMinutes` | Activity retention `30` days; seen-recently window `15` minutes |

Basic LDAP binds require TLS. Deliberately configured Negotiate LDAP without SSL
uses signing/sealing. Some comments shipped in 0.10.3's configuration mention
HTTP fallback or certificate bypass: those comments are stale; production
enforcement rejects those insecure settings.

## Deploy the client

### Interactive installation

Download the client MSI from the OPD dashboard. **Keep its generated filename**:
the hostname and optional certificate thumbprint configure the installation.
A generic GitHub MSI needs explicit configuration. Filename-based configuration
assumes HTTPS port 5443; use an explicit `SERVER` property for a different port.

Install with administrator rights, then sign in as the target ordinary user.
Check the tray menu's server, identity and version, select **Sync now**, verify
the connections and print a real document.

Domain/Entra-joined clients attempt Windows integrated sign-in. Standalone
clients expose **Sign in…** for domain credentials stored in that user's Windows
Credential Manager. OPD sign-in does not replace Windows permissions or network
authentication to the actual print share.

### Intune or another managed deployment system

Package the MSI for your management system and install in device/SYSTEM context.
The dashboard supplies a deployment command. If renaming the MSI to a generic
filename, pass the configuration explicitly, for example:

```cmd
msiexec /i "OpenPrintDeploy.Client.msi" SERVER="https://printsrv01.example.com:5443" ALLOWEDSERVERS="printsrv01.example.com,printsrv01" /qn /norestart /L*v "%TEMP%\opd-client-install.log"
```

For a pinned self-signed certificate, add
`CERTTHUMBPRINT="<actual-thumbprint-from-server>"`. Omit it for normal certificate
validation with a trusted CA. Replace example names before use.

| MSI property | Meaning |
| --- | --- |
| `SERVER` | Full OPD HTTPS URL |
| `CERTTHUMBPRINT` | Optional exact OPD certificate pin |
| `ALLOWEDSERVERS` | Allowed UNC hosts; defaults to OPD URL host. Short names and FQDNs are distinct entries in 0.10.3 |
| `REMOVEMANAGEDPRINTERS` | `1` by default; set `0` **at installation** to preserve connections on uninstall |

The uninstall-removal property does not disable normal sync removal. On upgrades,
supply the existing URL, pin, allow-list and uninstall policy again, or retain the
correct configured filename. Do not assume a renamed MSI retains previous settings.

For Intune, use MSI product/version metadata to detect the intended version;
an executable's presence alone does not distinguish an old client. Installation
is per-machine but the tray runs per-user. Verify a SYSTEM installation again at
user sign-in. Do not configure the tray to run as administrator.

Uninstall through Apps & Features or the installed version's MSI/product code:

```cmd
msiexec /x "OpenPrintDeploy.Client.msi" /qn /norestart /L*v "%TEMP%\opd-client-uninstall.log"
```

By default, cleanup removes OPD-created connections in user context, including
at next logon for absent users. Adopted connections and installed shared driver
packages remain. Major upgrades skip the uninstall printer-cleanup action.

## Driver management

**0.10.3 server + client enables automatic driver preparation.** The Local SYSTEM
service installs an exportable signed package before the tray connects a queue.
Updating the print server driver makes it eligible for delivery on later syncs;
there is no separate driver approval or deployment-ring control inside OPD.
Pilot driver updates before changing a production server used by many clients.

- Administrator-only driver policy remains enabled; OPD uses its privileged
  service rather than granting users installation rights.
- A valid signature does not automatically approve a publisher for unattended
  installation. OPD does not import publisher certificates itself.
- Supported scope is Windows x64, local-server queues and exportable INF-backed
  packages. Remote export, vendor setup EXEs and automatic rollback are unsupported.
- Existing assigned connections are also checked for driver updates. Preparation
  failure blocks the affected add; it does not by itself request deletion of an
  existing connection.
- Windows protected print mode blocks third-party drivers and is not supported
  by this vendor-driver delivery path. See
  [Microsoft's protected print guidance](https://learn.microsoft.com/en-us/windows/modern-print/windows-protected-print-mode/more-information-on-windows-protected-print-mode-for-enterprises-and-developers).

**Separate deployment:** 0.9.17 clients retain their existing Windows Point and
Print behaviour and can use appropriately preinstalled drivers, subject to policy
and version compatibility. They do not acquire the driver service from a server
upgrade. A dedicated externally managed drivers mode for 0.10.3 clients has
**not** been implemented. `DriverUpdatesEnabled=0` or stopping the service pauses
preparation; it is not a supported bypass for new connections.

Read [driver delivery, trust and failure handling](docs/driver-delivery.md) before
rollout. Keep Windows restrictions enabled; signed packages can still contain
vendor bugs, and a trusted server compromise remains a risk.

## Upgrade and version compatibility

| Server / client | Expected behaviour |
| --- | --- |
| **0.10.3 / 0.9.17** | Compatible sync contracts and Windows authentication; old driver behaviour remains |
| **0.10.3 / 0.10.3** | Current sync and automatic driver preparation |
| **0.10.2 / 0.10.3** | Compatible; client reliability fixes do not require a server upgrade |
| **0.9.17 / 0.10.3** | No driver tickets; silent Windows connection can fail if driver installation needs elevation |

Compatibility is based on code/contracts and automated coverage; it is not a
guarantee for every combination of policies, profiles and drivers. Do not
generalise the 0.9.17 statement to every older beta or 0.9 release.

Compared with 0.9.17, the 0.10 series adds driver delivery, forms admin login,
faster zone editing and stricter transport/storage security. 0.10.2 fixes the
legacy storage-permission upgrade failure. 0.10.3 fixes driver-service response
delivery and adds a limited silent connection retry. Its server MSI also bundles
the new client. See the [0.10.3 release notes](https://github.com/LouisCallander05/OpenPrintDeploy/releases/tag/v0.10.3).

### Server-first upgrade checklist

1. Record versions, URL, certificate thumbprint, custom settings, admin access,
   zones, global removals and a known user's current connections.
2. Make a recoverable backup as described below. Keep the previous installer.
3. On a test server, install the new **Server MSI over the existing installation**.
   Do not uninstall first or regenerate the certificate during an ordinary upgrade.
4. Check service startup, configuration, admin login, directory health, printers,
   zones and assignments before broad client access.
5. Leave a test client on **0.9.17**. Sync, print, assign an extra test queue whose
   driver is already installed, then reboot and repeat. Keep driver versions
   unchanged during this compatibility check.
6. Test a new client separately, including an absent driver, driver updates,
   user switching and restarts. Use the [acceptance checklist](docs/operations.md#release-acceptance-checklist).
7. Deploy during a maintenance window, verify a small pilot, then widen access.

Old plain-HTTP clients need HTTPS URLs and trust before moving to 0.10.3. A
service-start failure is not automatically an account-permission problem:
inspect the MSI and Windows logs.

## Backups and recovery

The default database is `C:\ProgramData\OpenPrintDeploy\app.db`; admin grants are
in `admin-access.json` beside it. Application data is outside the installation
directory and is intended to survive upgrades/uninstall. The installer updates
application files; database migrations run at service startup.

For a pre-upgrade backup, stop **OpenPrintDeployServer**, then capture a verified
server/VM backup or a consistent copy of the full OPD data directory and custom
configuration. Include custom database locations, the HTTPS certificate and
private key/PFX, and protected driver keys. Secure backups as administrator data.
Restart the service if continuing normal operation before the maintenance window.

Automatic pre-migration `.bak` copies are best effort: migration continues if
copying fails. They are **not** a replacement for an independent backup.

Restore the previous application and its matching database/configuration together;
do not assume an old MSI can read a migrated database. A full server restore
does not undo laptop connections or installed drivers. Restored assignments may
reconnect printers on a successful sync; client driver rollback is separate.
Verify the restored server before resuming client sync.

See [backup inventory and recovery steps](docs/operations.md#backup-and-recovery).

## Where to look when something fails

| Component | Location |
| --- | --- |
| Client tray log | `%LOCALAPPDATA%\OpenPrintDeploy\opd-client.log` in the **affected user's** profile |
| Client managed state | `%LOCALAPPDATA%\OpenPrintDeploy\managed-printers.json` |
| Client machine settings | 64-bit `HKLM\SOFTWARE\OpenPrintDeploy\Client` |
| Server / driver service errors | Event Viewer → Windows Logs → Application; service failures also in System |
| Windows printing events | Event Viewer → Applications and Services Logs → Microsoft → Windows → PrintService → Admin |
| Installer failure | Verbose MSI log created using `/L*v` |
| Assignments and sync reports | OPD **Directory**, **Zones**, **Printers**, **Clients** pages |

Use the [troubleshooting runbook](docs/operations.md#troubleshooting) for startup
failures, LDAP errors, trust, missing queues, FQDN failures and spooler crashes.
Do not erase client state, reset trust or relax Windows policy as a first step.

## Build and develop

Use Windows and the .NET SDK specified by `global.json`. To reproduce a release,
check out its tag in a separate clean checkout/worktree. Building requires the
SDK and NuGet access.

```powershell
dotnet restore
dotnet test OpenPrintDeploy.sln -c Release
.\scripts\Publish-Server.ps1 -Version 0.10.3
```

The script builds both MSIs and bundles the client into the server payload.
Outputs are `publish/OpenPrintDeploy.Server.msi` and
`publish/OpenPrintDeploy.Client.msi`. WiX restores through NuGet. Use a new
prerelease version for modified code rather than presenting it as unchanged 0.10.3.

For local development without AD:

```powershell
Copy-Item src/OpenPrintDeploy.Server/appsettings.Development.json.example `
  src/OpenPrintDeploy.Server/appsettings.Development.json
dotnet run --project src/OpenPrintDeploy.Server
```

The launch profile selects Development at `http://localhost:5080/`, using stub
directory data and development authentication. Never deploy this mode as a
production service. Automated tests do not replace MSI upgrade tests or physical
printing with the organisation's drivers and policies.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/OpenPrintDeploy.Server` | Web console, API, directory, SQLite and driver export |
| `src/OpenPrintDeploy.Client.Tray` | Per-user tray and printer connections |
| `src/OpenPrintDeploy.Client.Service` | Privileged driver preparation in 0.10 releases |
| `src/OpenPrintDeploy.Client.Core` | Reconciliation, state contracts and policy |
| `src/OpenPrintDeploy.Shared` | Contracts, driver helpers and installer naming |
| `installer/` | MSIs, EXE installer and uninstall cleanup |
| `scripts/` | Publishing and optional signing |
| `tests/` | Automated verification |
| `docs/` | Operations, architecture and implementation notes |

## Site handover record

Keep these in your organisation's protected support documentation: server FQDN/
URL, versions, admin group and recovery owner, certificate location/thumbprint/
expiry, directory endpoint/search base, database path, zone/group ownership,
approved driver versions and publishers, deployment commands/detection rules,
backup job/restore procedure, and last successful pilot date. Store passwords and
private keys in the approved secret store, not GitHub issues or this README.
