# D365FO Data Comparison Tool

Windows WPF configuration MVP for signing in, discovering Power Platform environments linked to Finance and Operations, selecting Source and Target, validating each OData connection, and saving the local configuration.

The environment discovery endpoint is Preview. Its response is isolated behind `IEnvironmentDiscoveryProvider`; the tenant POC must confirm environment coverage, `finOpsMetadata.url`, pagination, consent, and delegated access before the endpoint is relied on operationally. Metadata discovery, extraction, and data comparison are not part of this MVP.

## Prerequisites

- Windows 10 or later.
- .NET 10 SDK.
- A single-tenant Microsoft Entra app registration configured as a desktop/public-client application.
- Delegated Power Platform API permission `EnvironmentManagement.Environments.Read` with consent granted as required by the tenant.
- A Finance and Operations user account provisioned and authorized in both environments.

For Windows Account Manager (WAM), register the broker redirect URI `ms-appx-web://Microsoft.AAD.BrokerPlugin/{client-id}` for the app. WAM must be parented to the desktop window; the application does this for interactive sign-in.

For the Finance and Operations call, register the client application in each FO environment as required by that environment's setup. The delegated token must target the environment URL; the application requests `<finOps-url>/.default`, normalized to lowercase without a trailing slash. Power Platform discovery access alone does not grant FO access.

The app reuses the signed-in MSAL account and first tries to acquire an FO-audience token silently. It cannot reuse the Power Platform access token because that token has a different audience. On first FO use, an interactive consent/authentication step may still be required if the tenant hasn't granted the FO permission or the account needs to reauthenticate.

## Local configuration

Create `src/D365FO.Compare.Desktop/appsettings.local.json` for local settings. It is git-ignored and overrides `appsettings.json`. Set the Entra `Authentication:ClientId` and `Authentication:TenantId`. The app registration identifiers are not credentials; the public client uses no client secret. Keep access tokens, refresh tokens, passwords, and secrets out of all configuration and logs.

For example, the local file shape is:

```json
{
	"Authentication": {
		"ClientId": "<entra-client-id>",
		"TenantId": "<entra-tenant-id>"
	},
	"Environment": {
		"RequestTimeoutSeconds": 30
	},
	"PowerPlatform": {
		"ApiVersion": "2024-10-01",
		"Scope": "https://api.powerplatform.com/.default",
		"MaxDiscoveryPages": 100,
		"RequestTimeoutSeconds": 30
	},
	"Poc": {
		"SanitizedDiagnosticsEnabled": false
	}
}
```

The host policy is populated at runtime from FinOps URLs returned by authenticated discovery or explicitly added in the tool; no host list belongs in appsettings. Only HTTPS base URLs are approved; non-default ports, paths, query strings, and user info are rejected. Keep sanitized diagnostics disabled except during an approved POC. When enabled, logs contain only operation, status, page/attempt counts and elapsed/retry timings. They never contain a token, URL, environment name/ID, or response payload.

MSAL's user cache is stored under `%LOCALAPPDATA%/D365FODataComparisonTool/Auth/msal.cache` and protected with Windows DPAPI for the current user. The saved Source/Target configuration is versioned JSON under `%LOCALAPPDATA%/D365FODataComparisonTool/configuration.json` and contains no tokens. Logs are written to `%LOCALAPPDATA%/D365FODataComparisonTool/Logs`.

Manual entry accepts a base HTTPS URL only when the user explicitly adds it in the tool. A successful FO connection test is still required before saving.

## Tenant POC sequence

1. Run the local build and fake-handler tests first. Configure the Entra registration locally; environment hosts are populated from discovery or explicit tool configuration.
2. With the Entra admin, grant the delegated Power Platform discovery permission. Run one discovery in the intended tenant; retain only a reviewed, sanitized summary of counts, pages, status, retries and timing.
3. Before any FO request, have an FO admin/owner confirm whether the client ID must be mapped in that FO environment and confirm the user's least-privilege access. Without that confirmation, stop at discovery-only.
4. Test sandbox first. The connection check sends only `GET /data`; it does not read entity sets or records. Verify the expected `aud` and `tid` in memory only; never export or retain the JWT.
5. Test PROD only after sandbox passes and the PROD owner's approval and user authorization are reconfirmed. Perform one independent `GET /data`.
6. Sign out and clean up local configuration, logs and temporary evidence according to policy. Treat the DPAPI cache as authentication material and follow the security owner's disposal procedure.

Stop on an unexpected tenant/host, invalid `nextLink`, page-limit exhaustion, 401/403, repeated 429, or timeout. Do not retry authorization failures or increase privileges to force success. No raw API payload, token, or business data is POC evidence.

## Build, test, run

```powershell
dotnet build D365FO.DataComparisonTool.sln
dotnet test D365FO.DataComparisonTool.sln
dotnet run --project src/D365FO.Compare.Desktop/D365FO.Compare.Desktop.csproj
```

Unit tests use fake HTTP handlers and do not require tenant credentials or network access.