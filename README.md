# OpenPrintDeploy

Centralised deployment of shared printers to Windows users, based on Active
Directory group membership.

OpenPrintDeploy runs on a Windows print server and provides a web admin console
for importing shared printers, assigning them to zones, and monitoring clients.
A per-user Windows tray client periodically resolves the signed-in user's zones
and keeps their printer connections in sync.

## Features

- Import shared printers from the Windows print spooler or add them by UNC path.
- Group printers into zones and assign each zone to one or more AD groups.
- Install and remove per-user printer connections automatically.
- Preserve existing printers that OpenPrintDeploy did not install.
- Track client status, versions, printer state, sync failures, and removal history.
- Authenticate domain- and Entra-joined clients with Windows Integrated
  Authentication.
- Support workgroup clients through an explicit domain sign-in stored in Windows
  Credential Manager.
- Serve the client MSI directly from the admin console, preconfigured for its
  server and self-signed certificate when required.
- Use HTTPS by default, with either an automatically provisioned certificate or
  an administrator-supplied PFX.

Windows remains responsible for printer drivers and Point and Print policy.
OpenPrintDeploy manages connections to existing `\\server\share` printer queues.

## Requirements

- A 64-bit Windows print server with reachable shared printer queues.
- Active Directory and a reachable domain controller. LDAPS on port 636 is the
  default directory configuration.
- Windows client devices that can reach the server on TCP 5443.
- The .NET 8 SDK only when building from source. Release installers are
  self-contained and do not require a separately installed .NET runtime.

## Install the server

1. Download `OpenPrintDeploy.Server.msi` from the
   [latest release](https://github.com/LouisCallander05/OpenPrintDeploy/releases/latest).
2. Install it interactively, or run:

   ```cmd
   msiexec /i OpenPrintDeploy.Server.msi /quiet
   ```

3. Open **OpenPrintDeploy Admin** from the Start Menu or browse to:

   ```text
   https://<print-server>:5443/
   ```

4. Complete first-run setup to restrict administration to the appropriate AD
   group or users.
5. On the **Directory** page, test the LDAP connection. Then import printers,
   create zones, and assign AD groups.

The MSI installs the `OpenPrintDeployServer` Windows service, opens TCP 5443 in
Windows Firewall, and creates Start Menu and desktop shortcuts. Application data
is stored in `C:\ProgramData\OpenPrintDeploy` and is retained during upgrades and
uninstall.

The server is HTTPS-only by default. It creates a long-lived self-signed
certificate on first start unless `Https:PfxPath` is configured. Trust that
certificate on administrator workstations and clients, use the thumbprint
pinning included in the generated client installer, or provide a certificate
issued by a trusted CA. Configuration is in:

```text
C:\Program Files\OpenPrintDeploy\appsettings.json
```

See [Architecture and security](docs/architecture.md) for LDAP overrides,
certificate management, authentication, persistence, and migration details.

## Deploy the client

Download the client MSI from the server dashboard. The downloaded filename
contains the server hostname and, for a self-signed certificate, its thumbprint,
so double-clicking it configures the client automatically.

For Intune, rename the downloaded file to `OpenPrintDeploy.Client.msi`, wrap it
as a Win32 app with
[IntuneWinAppUtil](https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool),
and use the install command shown on the dashboard. It has this form:

```cmd
msiexec /i "OpenPrintDeploy.Client.msi" SERVER="https://printsrv01.example.com:5443" CERTTHUMBPRINT="AB12..." /qn
```

The `CERTTHUMBPRINT` property is needed only for the server's self-signed
certificate. For a certificate issued by a CA already trusted by clients, omit
it.

Use this uninstall command:

```cmd
msiexec /x "OpenPrintDeploy.Client.msi" /qn
```

The MSI installs the tray application under
`C:\Program Files\OpenPrintDeploy\Tray` and starts it in each user session. By
default, uninstall removes printer connections created by OpenPrintDeploy while
leaving printers that existed before OpenPrintDeploy adopted them.

## Build and develop

The .NET SDK version is pinned in `global.json`. On Windows:

```powershell
dotnet restore
dotnet test OpenPrintDeploy.sln
```

To run the server locally without Active Directory, copy the development
configuration template and start the project:

```powershell
Copy-Item src/OpenPrintDeploy.Server/appsettings.Development.json.example `
  src/OpenPrintDeploy.Server/appsettings.Development.json
dotnet run --project src/OpenPrintDeploy.Server
```

The development admin console is available at `http://localhost:5080/` and uses
stub users, groups, printers, and header-based development authentication. This
mode must not be used for a deployed server.

To build the self-contained server MSI, including the client MSI served by the
dashboard:

```powershell
.\scripts\Publish-Server.ps1
```

The output is `publish/OpenPrintDeploy.Server.msi`. The WiX SDK and extensions
restore through NuGet; no global WiX installation is required.

## Repository layout

```text
src/
  OpenPrintDeploy.Server       ASP.NET Core server, API, and Blazor admin console
  OpenPrintDeploy.Client.Tray  WPF per-user tray client
  OpenPrintDeploy.Client.Core  Client sync and reconciliation logic
  OpenPrintDeploy.Shared       Shared API contracts and installer naming
installer/
  OpenPrintDeploy.Server.Msi   Server WiX package
  OpenPrintDeploy.Client.Msi   Client WiX package
  OpenPrintDeploy.Client.Cleanup
                               Per-user printer cleanup helper
scripts/                       Publish and signing scripts
tests/                         Server, client-core, and shared tests
docs/architecture.md           Architecture, security, and operational details
```
