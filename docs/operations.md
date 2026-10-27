# Technician operations runbook

Applies to **0.10.3**, with 0.9.17 client compatibility notes. Start with the
[README](../README.md) for installation, configuration and removal behaviour.
Commands below use example names; replace them with your site's values. Run
user printer checks in the affected user's session, not an administrator's
different profile. Service/configuration changes require administrative rights.

## Routine administration

- **Printers:** import working Windows shares. Verify the UNC share name, which
  can differ from the Windows queue's display name. Registering a queue in OPD
  does not create the underlying Windows queue or its physical printer port.
- **Zones:** assign printers and AD group SIDs. Rules within a zone match with
  OR semantics; matching zones contribute a combined printer list. A rename of
  an AD group does not change its SID; deleting and recreating a group does.
- **Directory:** test connectivity and inspect user access. Sync normally uses
  group SIDs in the authenticated token, with directory lookup as a fallback.
  Browser admin identity is not necessarily the affected client identity.
- **Clients:** inspect per-device, per-user results. "Online" means recently
  seen, not a live reachability check. A successful sync is not proof that a
  physical print job completed.
- **Settings:** manage admin grants and inspect connection security. Record
  certificate rotation as a deployment change, including client pins.
- **Tray:** manual sync shows a result. During an existing sync, the result
  action subscribes to that cycle instead of starting a second one.

Group labels are cached and can lag an AD rename. Zone editing uses already
loaded data while labels resolve in the background. Authentication tokens can
also retain old memberships; test a new user sign-in after group changes. Admin
sessions retain sign-in-time group claims for up to eight hours.

## Paths and services

These are default paths; inspect custom configuration before copying or restoring.

| Item | Location / name |
| --- | --- |
| Server service | `OpenPrintDeployServer`, Local SYSTEM |
| Client driver service | `OpenPrintDeployDrivers`, Local SYSTEM, 0.10 clients |
| Windows printing service | `Spooler` |
| Server installation/configuration | `C:\Program Files\OpenPrintDeploy\appsettings.json` |
| Client installation | `C:\Program Files\OpenPrintDeploy\Tray` |
| Server database and admin grants | `C:\ProgramData\OpenPrintDeploy\app.db`, `admin-access.json` |
| Driver ticket protection keys | `C:\ProgramData\OpenPrintDeploy-DriverKeys` (machine-protected) |
| Server exported package cache | `C:\ProgramData\OpenPrintDeploy-DriverPackages` |
| Client protected staging | `C:\ProgramData\OpenPrintDeploy-DriverClient` |
| Client settings | 64-bit `HKLM\SOFTWARE\OpenPrintDeploy\Client` |
| User log/state | `%LOCALAPPDATA%\OpenPrintDeploy\opd-client.log`, `managed-printers.json` |
| User uninstall log | `%LOCALAPPDATA%\OpenPrintDeploy\opd-uninstall.log` when written |

For MSI clients, the registry holds `ServerBaseAddress`,
`ServerCertificateThumbprint`, `AllowedPrintServers`, `InstallerSource` and
`RemoveManagedPrintersOnUninstall`. Blank URL/pin values can be resolved from
`InstallerSource`'s configured filename. Tray environment overrides and older
EXE-installed `appsettings.json` can also affect configuration. The privileged
driver service ignores user environment overrides, so changing only the tray's
environment can leave it pointing at a different server from the service.

## Troubleshooting

### Capture evidence first

Record the server and tray versions, affected user/computer, exact UNC, timestamp,
error code, and whether this started after an upgrade, driver change or profile
change. Preserve the tray log around startup and one failed sync. Windows event
XML timestamps are UTC; the tray's timestamps are local time.

On the **affected client as the affected user**, these checks are read-only:

```powershell
whoami
Get-Service Spooler, OpenPrintDeployDrivers -ErrorAction Continue |
    Format-Table Name, Status
Get-Printer | Format-Table Name, DriverName, Type
Get-Printer -ComputerName 'printsrv01.example.com' |
    Format-Table Name, ShareName, Shared, DriverName
Get-PrinterDriver | Format-Table Name, MajorVersion, InfPath
Get-Content "$env:LOCALAPPDATA\OpenPrintDeploy\opd-client.log" -Tail 100
```

`OpenPrintDeployDrivers` is absent on a normal 0.9.17 client. Remote queue
enumeration can fail due to access/policy; that is useful evidence, not a reason
to immediately elevate the user. If PrintManagement commands are unavailable in
PowerShell 7, use Windows PowerShell or the equivalent Windows printer UI.

On the **server**, check:

```powershell
Get-Service OpenPrintDeployServer, Spooler | Format-Table Name, Status
Get-Printer | Format-Table Name, ShareName, Shared, DriverName
```

Inspect Event Viewer **Application** and **System**, and client/server
**Microsoft-Windows-PrintService/Admin** around the same time. For an installation
failure, retain the `/L*v` MSI log; inspect the failure context around
`Return value 3`, not just the final generic installer message.

Before sharing evidence, redact passwords, private keys, bearer driver tickets
and unrelated organisational details. Never post a complete PFX, credentials or
unredacted sensitive configuration in an issue.

### Symptom guide

| Symptom | Next checks |
| --- | --- |
| MSI says service failed to start / insufficient privileges | MSI log plus Application/System events; HTTPS certificate/private key, configuration, storage ACL/owner errors |
| Upgrading an older server fails on storage permissions | 0.10.2+ includes a narrowly scoped legacy ACL migration; unexpected owners/broad write grants still fail. Preserve the error and investigate the actual path/ACL |
| Admin login fails or stops working | Confirm qualified identity and grants, Windows logon error, domain connectivity and HTTPS; service restart intentionally invalidates sessions |
| Directory returns zero groups | Check whether this is a successful empty lookup or failed connection, the domain prefix/search base, and actual group membership |
| Driver error `0x800F0242` | Publisher is not established as trusted for installation; inspect the actual package catalogue and client machine publisher trust |
| "Unable to read beyond the end of the stream" on 0.10.1/0.10.2 | Known driver-service response defect fixed in the **0.10.3 client**; upgrading only the server does not replace laptop clients |
| Error 3012 / `0x80070BC4` / printer not found | Compare exact FQDN and short-name connection in the same user session; verify share, auth, allow-list, driver and printing events |
| Printers disappear | Compare Windows printer list with OPD managed state, zone changes, global removals, uninstall activity and spooler events |
| Tray appears hung during sync | Capture version/logs and current sync state; check server/service timeouts and whether multiple tray processes are running |
| `spoolsv.exe` crashes | Preserve crash/PrintService events and driver versions; a fault in `localspl.dll` alone does not establish the cause |

Do not resolve storage errors by granting ordinary Users write access to OPD's
protected directories. Do not treat every service error as an installer elevation
problem, or every Windows printer error as a driver-download failure.

### Directory and LDAPS

LDAPS on the DC and HTTPS on OPD are separate services. For the default
`UseSsl=true`, port `636` configuration:

1. In OPD's **Directory → Test connection**, record the effective DC FQDN,
   search base and error. A failed lookup alone does not prove LDAPS is disabled.
2. On the DC, use `certlm.msc` to inspect the intended LDAPS certificate. It needs
   a usable private key, Server Authentication usage, a valid date and the DC's
   FQDN in the certificate. AD DS can also use its service certificate store.
3. On the OPD server, ensure the issuing root/intermediate CAs are trusted by
   the local computer. A certificate trusted only by the administrator's user
   account is not sufficient evidence for the SYSTEM service.
4. From the OPD server, use `ldp.exe` (install the relevant administrative tools
   if absent): **Connection → Connect**, DC FQDN, **636**, **SSL** selected.
   Test **Connection → Bind** as well. An administrator's successful bind does
   not establish that OPD's service identity works; repeat OPD's own test.
5. Confirm search base and domain routing, then retry the intended user's access.

Microsoft documents the certificate requirements and validation procedure in
[Enable LDAP over SSL](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/enable-ldap-over-ssl-3rd-certification-authority).
No certificate templates in the enrollment wizard can mean no reachable enterprise
CA or no eligible published template; it is not fixed by OPD. Use your existing
PKI administrator/process rather than installing a production CA just for a test.

Never enable `AllowInvalidCertificate`; 0.10.3 rejects it. Basic LDAP without TLS
is also rejected. For intentional Negotiate LDAP on 389, OPD requests signing and
sealing; validate that configuration against department policy.

### Signed driver but publisher not trusted

Check the package's **`.cat` catalogue**, not just the INF's Properties window.
An INF can rely on a catalogue signature and have no embedded signature. On the
client, inspect the catalogue's signature details and certification path.

A valid signature and an approved Trusted Publisher are different checks. After
verifying the package source, signer and thumbprint through your organisation's
process, an administrator can distribute the approved **signer's public
certificate** to **Local Computer → Trusted Publishers**, scoped to the intended
devices. Do not put a vendor signing certificate into Trusted Root, import private
keys, or substitute the OPD/LDAPS certificate. See
[Microsoft Trusted Publishers guidance](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/trusted-publishers-certificate-store).

Keep administrator-only print-driver installation enabled. Microsoft's
[Point and Print guidance](https://support.microsoft.com/en-gb/servicing/os/windows/2021/08/kb5005652-manage-new-point-and-print-default-driver-installation-behavior-cve-2021-34481)
explains the default restriction. Signature approval does not grant standard
users permission to install drivers; installation still needs the privileged path.

### FQDN connection fails but short name works

Ping only establishes name resolution/basic reachability, not successful Windows
printing authentication or RPC. Successful `Get-Printer -ComputerName` enumeration
also does not prove a user can create a connection.

Compare the exact same queue with the same user through Windows Explorer using
`\\printsrv01.example.com` and `\\printsrv01`. **Connecting is a deliberate test
change**, unlike listing queues; use a pilot machine. Check which host is in OPD's
UNC and in the machine allow-list. Test another profile and another machine to
separate per-user from machine/server behaviour. Kerberos tickets, saved Windows
credentials and effective policy can differ between profiles.

In the 0.10.3 pilot, a different user profile worked while the original profile
also failed through Windows Explorer. The original cause remains unresolved.
Neither `inetpp.dll` Event 512 nor computer-name length alone proves the cause.
Do not clear all credentials, rebuild the profile or rename the PC without
evidence and a recovery plan. The 0.10.3 silent retry is not a general repair
for FQDN/profile failures.

### Missing queues or a spooler crash

Check `Get-Printer` in the affected user's session and distinguish an application's
print list from Windows connections. Preserve `managed-printers.json` before
changing it; deleting it loses OPD's ownership history. Check global removal
entries as well as zone assignments. An authoritative empty assignment can remove
managed connections even though the OPD server is reachable.

For a reproducible spooler crash, capture Application Error/Windows Error Reporting
and PrintService evidence, then reproduce on an isolated client with the same
driver. If crash dumps are needed, arrange bounded collection and secure storage:
dumps can contain document/user data. Restarting the spooler disrupts jobs and does
not diagnose the cause. Do not automatically delete all queues or driver packages.

### Admin access recovery

For a locked-out administrator with authorised server access, back up the config
and merge a qualified recovery user or group SID into its existing `Auth:Admin`
section. For example, the relevant fragment is:

```json
{
  "Auth": {
    "Admin": {
      "Users": ["EXAMPLE\\opd-recovery-admin"]
    }
  }
}
```

This is a **fragment**, not a replacement for the complete configuration. Preserve
other grants/settings, validate the JSON, and restart `OpenPrintDeployServer`.
After recovering access, review the normal admin group and remove temporary grants
according to your access process. Do not delete `admin-access.json` to reopen setup:
an unreadable grant store deliberately fails closed.

## Backup and recovery

Schedule a maintenance window and identify which clients may sync during it.
If assignments appear wrong after a change, contain further sync access while
investigating. Stopping only `OpenPrintDeployDrivers` does **not** stop tray sync
or assignment removal. Stopping `OpenPrintDeployServer` stops new sync responses;
it cannot recall a response already received by a client.

Before upgrading, stop the OPD server service and capture a verified backup:

- Entire default ProgramData OPD data directory, including `admin-access.json`
  and any SQLite `-wal`/`-shm` files present. Include the actual custom database
  path if `ConnectionStrings:AppDb` overrides the default.
- Custom `appsettings*.json`, relevant service/environment configuration and the
  previous installer/version.
- HTTPS certificate **with its private key**, or configured PFX and a separately
  secured password. Record thumbprint, expiry and client pin distribution.
- Protected `OpenPrintDeploy-DriverKeys` and Windows machine state for a full
  restore. DPAPI-protected files alone are not a portable new-server migration.
- Windows print-server queues, ports, drivers and settings if they are not already
  included in the full server backup. The OPD SQLite database does not contain them.

A verified full VM/server backup can capture these together. Keep the OPD service
stopped until its backup point has been captured; restart it if returning to normal
operation before the upgrade. Confirm backup success and the actual restore path.
Automatic `.bak` copies during migrations are only a secondary convenience.

For rollback, restore the matching old application, database, configuration and
certificate together. Validate directory access, admin grants, zones, explicit
removals and a pilot user before reopening sync to the fleet. A full restore
also loses server changes since that backup. Avoid booting a restored copy with
the original server's identity on the production network alongside the original.

Laptop printer connections and installed drivers are not rolled back by a server
restore. A restored assignment may reconnect a missing queue at next sync, subject
to driver/authentication readiness. There is no automatic client driver rollback.

## Release acceptance checklist

Record the exact MSI version/hash, Windows builds, policies, driver versions,
test user, date, result and evidence for each case. Use the exact release candidate
MSIs. An existing lab machine with preinstalled drivers cannot establish that a
clean installation works.

Use a test print server, a client left on 0.9.17 and a recoverable clean client for
the new version. Apply representative department policies and use standard users.
Keep fresh-install and upgrade baselines separate. Test actual printers for each
driver family, including finishing/accounting features in use.

| Scenario | Pass condition |
| --- | --- |
| Baseline 0.9.17 server/client | Assigned printers sync and print before changes |
| Server-only upgrade to 0.10.3 | Old client still authenticates, retains assignments and prints; config/cert/grants remain correct |
| Zone assignment change on pilot | Only intended additions/removals occur; unrelated connections remain |
| Global removal on pilot | Only the deliberately targeted UNC is removed, including an unmanaged test connection |
| Fresh client, absent driver | Signed package prepares and standard user connects/prints without admin prompt |
| Existing client MSI upgrade | Settings and connections retained, tray/service version correct, reboot works |
| Driver update | Intended package reaches pilot, existing/shared-driver queues still print with correct preferences |
| Failure cases | Server/DC unavailable, service stopped, untrusted/corrupt package and expired ticket produce bounded failures; no unintended removals |
| UI and concurrency | Manual/background sync and repeated clicks stay responsive; simultaneous sessions do not corrupt driver installation |
| Reboot/sign-out/service restart | Services recover, expected connections remain/reconcile, admin can sign in again |
| Uninstall policies | OPD-created versus adopted connections handled correctly, including next logon; keep-printers policy honoured; shared drivers retained |
| Backup restore | Previous application/data/config/certificate restored and pilot printing verified |

For directory-outage tests, ensure the directory fallback is actually exercised;
an authenticated token with group SIDs can continue resolving assignments without
a fresh LDAP lookup. For physical tests, verify colour/mono, duplex, trays and any
accounting code used by the site. A successful queue add or sync log is insufficient.

Automate contract, failure-handling and privilege-boundary tests in CI, and MSI/
service checks in disposable Windows VMs. Use physical print checks and a small
staff pilot over several working days for vendor behaviour. Keep unverified fixes
in prerelease and record any untested path rather than treating it as passed.
