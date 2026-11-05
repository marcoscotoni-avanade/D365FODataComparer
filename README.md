# D365FO Data Comparison Tool

Windows WPF tool for signing in, configuring Source and Target Finance and Operations environments, reading their metadata, and comparing their published OData schemas.

The environment discovery endpoint is Preview. Its response is isolated behind `IEnvironmentDiscoveryProvider`; the tenant POC must confirm environment coverage, `finOpsMetadata.url`, pagination, consent, and delegated access before the endpoint is relied on operationally. Business record extraction/comparison and FO writes are not part of this MVP.

## Metadata workspace

Save a configuration after successfully testing both connections. The app opens **Metadata & comparison**; returning to **Configuration** lets you change environments. Changing an environment/account invalidates in-memory schemas and results. No metadata is downloaded automatically.

- **Load / refresh both** reads Source then Target, avoiding concurrent authentication prompts. Each environment requires both `GET /data/$metadata` and `GET /Metadata/DataEntities` to succeed before a complete snapshot is published.
- **Catalogs** shows the OData EntitySets independently for Source and Target, with searchable names, FO identity, field count and mapping status. Select an entity for fields, ordered keys, enum/navigation/annotation details and observed versus missing facets. The FO-only catalog does not imply `IsPublic=false`.
- **Comparison scope** offers all entities or an explicit checkbox selection from the union of both catalogs. Search only changes visibility, never the selected scope. Entities missing from either side remain selectable.
- **Results** lists added, removed, changed and inconclusive entities with severity filters and property-level Source/Target values. `Breaking` describes schema restrictions, not a prediction of business validation behavior. Precision/scale changes are evaluated together.
- Unknown or invalid facets are not converted to false/zero. Standard defaults are recorded as effective values, separately from explicitly declared values. Incomplete evidence prevents full equivalence, even with equal core fingerprints. Navigation and annotation differences are informational and still compared when core hashes match.

`Microsoft.OData.Edm` 9.0.0 parses CSDL; XML DTDs and external entity resolution are disabled. EntitySet identity and field matching are ordinal/case-sensitive. Only a unique match on both public collection and public entity name assigns the FO identity; partial/ambiguous mappings remain visible with diagnostics. No pluralization, suffix stripping or fuzzy matching is used.

### Slow metadata downloads

Metadata uses a separate HTTP client with an infinite HTTP timeout by default, including the wait for response headers. The 30-second connection-test timeout does not apply. The UI remains cancelable while waiting, streaming and between parsing stages; the EDM parser itself is synchronous, so cancellation inside its parse pass takes effect when that pass returns. Server/proxy timeouts can still terminate a request.

The `Metadata` settings are independent of discovery/connection testing:

```json
"Metadata": {
	"TotalTimeoutMinutes": 0,
	"MaxPayloadBytes": 536870912,
	"MaxCatalogPages": 100
}
```

Zero disables the optional total timeout per endpoint/page. Downloads stream to a private per-user temporary file opened with delete-on-close; no full response string or permanent raw payload is retained. The default payload limit is 512 MiB per response, independent of download duration. TCP connection establishment is limited to 60 seconds, not the subsequent AOS response wait. HTTP 429/502/503/504 and pre-body network failures receive at most two retries; response-body failures are not replayed automatically. Redirects and next links to other origins/endpoints are rejected. Metadata HTTP factory logging is disabled to keep request URLs out of logs.

### Snapshot cache

Canonical snapshots are versioned JSON under `%LOCALAPPDATA%/D365FODataComparisonTool/Schemas`, keyed by tenant, account, environment identifier and HTTPS origin. Files include schema/provenance information, but no tokens or business records; they are not encrypted. Apply your organization's metadata-retention policy. The cache has an integrity hash, not a digital signature, and atomic replacement prevents a failed refresh from overwriting the previous snapshot.

At restart, cached schemas for the same account can be consulted and compared without downloading. Fresh online acquisition requires re-testing both connections and saving again. Capture times are visible. A failed refresh retains the previous schema with an explicit not-updated status. **Clear schema cache** removes the current pair independently of the connection configuration; **Clear saved configuration** does not delete all historical schema files.

Before relying on the REST catalog contract, authorize a read-only metadata POC for both environments. Verify actual JSON property names/envelope, mapping coverage and header/download/parse times. Permission for the earlier `GET /data` connection POC is not automatically permission to read these metadata endpoints. Do not retain real raw fixtures without explicit approval and anonymization. `/Metadata/PublicEntities` and developer-side AOT metadata remain outside this MVP.

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

Tests use fake HTTP handlers and do not require tenant credentials or network access. The WPF smoke test renders 2,000 synthetic entities at normal/minimum widths, checks binding errors and row virtualization, and writes screenshots beneath the test assembly's `TestResults` directory. Close the running app before a normal build; an isolated verification build can use `-p:BaseOutputPath=bin/metadata-check/` on both build and test commands.