# Automatic driver delivery — first implementation

The Windows x64 client now includes `OpenPrintDeployDrivers`, a Local SYSTEM
service installed by both client installers. It prepares drivers before the
user's tray connects printers. The tray checks every assigned queue at startup,
on the configured interval (default five minutes), and on manual sync. Updating
the driver on the print server is the deployment decision; no additional
approval or manual package upload is required.

This implementation needs a restricted-PC pilot before production rollout.
Compilation and automated tests do not establish compatibility with vendor
drivers or department policy.

## Requirements and supported scope

- Driver delivery requires a 0.10 server and client. Older clients continue to
  sync and ignore download tickets. With an older server, new clients attempt
  normal Point and Print silently and report failure if elevation is needed.
- OPD must run on the Windows print server hosting the queue. Its discovered
  UNC must match the registered printer UNC. Split-server queues are rejected
  by this first export implementation; adding a host to the allow-list alone
  does not enable remote export.
- The client service runs as Local SYSTEM on Windows x64. The server needs
  permission to export local driver packages; its standard Local SYSTEM
  installation satisfies this. Custom server service accounts need separately
  provisioned storage permissions and have not been validated for this feature.
- Drivers must have an INF-backed, exportable Driver Store package. Legacy
  unpackaged drivers, inbox packages that Windows cannot export, and packages
  needing separately distributed dependencies may fail. There is no unsigned
  or vendor-executable fallback. Failures leave existing connections installed.
- HTTPS with machine certificate trust or an installer-configured certificate
  pin is mandatory. Redirects are refused. No user credential is passed to the
  service, so this path does not require a domain computer account.
- `AllowedPrintServers` uses **exact host matching** in the privileged service.
  List short names and FQDNs separately when both are used. The default is the
  OPD URL's host. The service reads HKLM's 64-bit
  `SOFTWARE\OpenPrintDeploy\Client` and the protected install-directory
  `appsettings.json`; it ignores user environment overrides.

## Trust and failure behavior

Authenticated `/sync` responses issue 30-minute Data Protection tickets scoped
to a queue. The HTTPS manifest/download endpoints validate the ticket and check
that the queue remains registered and is not globally removed. Tickets are
bearer capabilities and can be reused during their lifetime; keep them out of
logs. Group reassignment takes effect for new tickets; existing tickets expire
within 30 minutes. Windows production servers persist DPAPI-protected keys in
`%ProgramData%\OpenPrintDeploy-DriverKeys`.

The service validates the queue against its own machine policy, fetches a
manifest and package only from the configured HTTPS origin, checks SHA-256 and
length, extracts within private storage, and lets Windows validate the complete
signed package. It never enables non-admin driver installation. A trusted
server compromise or a faulty signed driver remains a fleet-wide risk.

The service returns the installed driver's name, environment, version and date
after verifying them. The tray rechecks that identity and creates the per-user
connection with `AddPrinterConnection2W`, selecting the prepared local driver
for rendering (`PRINTER_CONNECTION_MISMATCH`). Package updates remain the
service's responsibility. Every connection attempt sets
`PRINTER_CONNECTION_NO_UI`: a policy block becomes a reported failure instead
of opening an administrator credential dialog. This flag alone does not make
a blocked installation succeed. Since 0.10.3, an explicit prepared-driver
connection returning error 3012 is retried once using standard Windows driver
selection, still with `PRINTER_CONNECTION_NO_UI`. Other errors are not retried
through this fallback. A missing or changed prepared driver still blocks both
attempts. Logs identify a successful fallback or report the final error if both
attempts fail. Validate rendering, finishing options and
server-side accounting with each vendor driver in the pilot.

Error `0x800F0242` means the package's Authenticode publisher is not yet trusted
on the client. A signed package is not necessarily approved for silent install.
An administrator must verify the actual package signing certificate and deploy
the approved signer to **Local Computer > Trusted Publishers**, or choose an
appropriately trusted driver package. For a lab GUI check, inspect the matching
vendor package's `.cat` file under **Properties > Digital Signatures**, view
the signing certificate and validate its chain and fingerprint before exporting
the public certificate. Import the verified signer with `certlm.msc` on the test
client into Trusted Publishers; for deployment use a computer GPO scoped to
the pilot machines. Do not substitute the LDAPS CA certificate. OPD does not
automatically trust certificates supplied by the print server or disable
signature checks. See Microsoft's
[Trusted Publishers documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/trusted-publishers-certificate-store).

IPC accepts local authenticated users and denies network clients. Requests and
downloads have size limits and deadlines. Driver installations are serialized
across user sessions. Native package installation runs in a separate process
with a two-minute deadline. The tray has a five-minute per-queue deadline; busy
or unavailable service requests fail visibly on manual sync and retry on the
next cycle. Since 0.10.3, the service waits for the client to read the response
and close its connection before disconnecting and reusing the pipe, within the
existing ten-second response deadline. This avoids discarding unread responses
while keeping the first pipe instance reserved against name squatting.
Windows spooler operations are not transactionally reversible:
timing out cannot guarantee that a spooler operation has not already committed.
The service does not automatically restart the spooler or roll back drivers.

Each installed package is tracked by its hash as well as the Windows driver
name, environment, date and version. An update using the same version metadata
still causes installation when its exported package changes. A server change
between manifest and download returns a conflict and is retried on next sync.

The server caches exports in `%ProgramData%\OpenPrintDeploy-DriverPackages`.
Packages are limited to 512 MB compressed and expanded. New exports stop after
the cache exceeds 4 GB; administrators can remove unused cached versions during
a maintenance window. Client staging is private to SYSTEM/Administrators under
`%ProgramData%\OpenPrintDeploy-DriverClient`. Completed staging directories are
removed; a crash may leave a directory for administrator cleanup. Uninstall
leaves installed drivers in place because other printers may use them.

## Operations

Set DWORD or string `DriverUpdatesEnabled` to `0` under the client HKLM key to
pause automatic preparation. Delete it or set it to `1` to resume. It is read
on every request; no restart is required. While paused, new connections needing
the preparation path wait and existing connections remain installed.

Driver-service events go to the Windows Application event log. Queue failures
are also logged at `%LOCALAPPDATA%\OpenPrintDeploy\opd-client.log`. The tray menu
shows sync progress and the last sync time/status. During background work,
"Syncing… (show result)" subscribes to the running result instead of starting a
second sync; sign-in changes are disabled until the cycle ends. Exit remains
available. Background cycles remain silent.

## Required pilot

1. Use a test print server and disposable/recoverable Windows x64 client. Enable
   the department's exact print-driver restrictions and keep them enabled.
2. Install matching server/client builds. Confirm `OpenPrintDeployDrivers` runs
   as Local SYSTEM, starts after reboot and survives a client upgrade.
3. As a standard user, sync a queue whose driver is absent. Verify a signed
   package is exported automatically, installed, and the user connection works
   without an elevation prompt. Test actual printing and printer preferences.
4. Update the queue driver on the server. Sync with an existing connection and
   confirm the client version changes. Test two queues sharing a driver and
   simultaneous users. Also test a server rollback/version mismatch deliberately.
5. During an intentionally slow/unreachable-server sync, right-click the tray,
   select the running-sync result action and exit. The menu must stay responsive
   and no duplicate sync should run.
6. Test invalid/expired tickets, an unlisted host, a corrupt/unsigned package,
   an unavailable service, a paused service and a server outage. Confirm existing
   connections remain and logs identify the failure without exposing tickets.
7. Exercise MSI and EXE install/upgrade/uninstall. Confirm service removal and
   existing printer-cleanup policy; shared installed drivers must remain.

Automatic tests cover ticket integrity/expiry, endpoint access, strict source
matching, archive path and size validation, and reconciliation on driver failure.
They do not replace this Windows/vendor/policy pilot.
