# D365FO Data Comparison Tool

Windows WPF tool for configuring Source and Target Finance and Operations environments, comparing their published OData schemas, and comparing explicitly scoped business records.

The environment discovery endpoint is Preview. Its response is isolated behind `IEnvironmentDiscoveryProvider`; the tenant POC must confirm environment coverage, `finOpsMetadata.url`, pagination, consent, and delegated access before the endpoint is relied on operationally. FO writes, actions, synchronization and automatic repair are not part of this MVP.

## Data comparison workspace

After saving validated connections and loading complete schemas for both environments, open **Data comparison** and press **Load setup**. The operating form and **Start data comparison** stay disabled until required setup completes. Load setup currently reads `LegalEntities` independently from Source and Target, requesting the string company code `LegalEntityId` and optional `Name` only when exposed in the schema. The command is a setup entry point for future setup requirements, not an automatic business-record comparison. No lookup or comparison request starts merely by opening the tab.

1. Select Source and Target companies from their separate loaded lists. Both display the code and optional description; the first row is never selected automatically. Check the entities to configure, then explicitly select `Company` or `Shared` scope for each. The bulk **Set Company** action uses the company field entered beside it; its initial `dataAreaId` text is only a candidate, not an inferred scope. **Set Shared** confirms that no company filter is required.
2. Leave **Business key override** empty to use compatible ordered OData keys, or provide an explicit comma-separated business key. All key fields must exist on both sides and cannot be excluded. Technical identifiers such as RecId can differ between environments; review them. Duplicate keys anywhere in the downloaded scope produce `DuplicateKey`, never an arbitrary first-row match.
3. Use the checkboxes in **Available field** to choose fields for download and comparison. **Select All** checks every row; **Clear Selected** clears all flags for the current entity. With no checkboxes, the request omits `$select` and downloads the complete record; exclusions apply only to comparison. With checkboxes, only allowed checked fields plus required key/company fields are requested and compared. **Required** identifies key/company fields without forcing their checkbox flags. Configure additional comma-separated exclusions and typed filters. Unsupported fields outside an explicit selection are not compared; in full mode they require explicit exclusion. String filters use literal text; numeric/boolean filters use JSON numeric/boolean values. Comparison remains strict: no fuzzy matching, string trim/case folding or numeric tolerance.
4. Choose **Selected entities** or **All eligible entities**. Review each entity's eligibility. Mass mode reports blocked/skipped names and does not claim complete coverage of the original catalog. Explicit selection can include blocked entities; those remain in the result with their blocking reason.
5. Review the limits and press **Start data comparison**. Confirm company pair, entity count, limits and metadata capture times in the dialog before any business records are read. **Cancel** remains available. A job reads Source then Target, one entity at a time.
6. In **Data results**, select an entity, filter the record status and page through results in batches of 100. Select a record for original and canonical values and value-state diagnostics. Export CSV/JSON only to an approved location; incomplete reports are explicitly marked. Equal records are excluded from export unless selected.

Use **Count rows** to request Source and Target counts for every entity in the loaded schema union, regardless of search text or entity checkboxes. **Count selected rows** counts only checked entities; it is disabled when none are selected. Counts use each entity's current Company/Shared scope and typed filters; **Not configured** leaves the endpoint's default company behavior unchanged. Both commands use OData `$count=true&$top=0` to request the count annotation with an empty value array, without downloading records or altering comparison results. A selected-only recount updates only those entities; counts remain visible when configuration or unrelated actions change until **Clear counts** is pressed or a later count replaces them. Failed or unavailable counts remain distinct from zero. Configure count-only HTTP concurrency in **General parameters**: default 2, valid range 1–8, saved per user and applied to the next count operation. Live F&O behavior with the entity-specific filters still requires confirmation against the target environments; if the zero-row query is unsupported or returns records, the row shows a diagnostic rather than silently falling back to record downloads.

The bulk **Force Eligibility** action adds fields missing from either side to **Excluded fields** for every currently non-eligible entity whose Source and Target OData keys have the same field names (key order may differ). Existing exclusions are retained. The action does not override different/missing keys, which are a hard prerequisite for comparison, or other remaining incompatibilities; review each entity's Eligibility after applying it.

### Setup and company selection

**Load setup**, **Cancel**, **Validate connections** and temporary-results cleanup remain accessible while the operating form is blocked. Setup fetches Source then Target, reusing the authenticated HTTPS host/account guards and cancellation. Lists require complete paging; invalid codes, conflicting duplicates, errors or cancellation are not published as a complete list. Lookup limits are 100 pages, 10,000 rows, 8 MiB per response and 32 MiB cumulative per side. The optional descriptions and list data remain in memory; temporary HTTP files use delete-on-close. No new URL, token or response logging is enabled.

**Setup diagnostics** identifies lookup failures. HTTP 401 or a changed account requires connection revalidation and cannot be bypassed with manual entry. If the LegalEntities lookup fails for another reason, or a saved code is not listed, **Manual Source / list** and **Manual Target / list** offer a separately confirmed fallback for that side. Manual companies are marked **UNVERIFIED** in the UI, run confirmation and report profile. This does not grant permissions on any FO entity. Other future required setup failures must still be resolved; company fallback does not waive them.

Profiles retain the company code strings. Loading a profile matches those codes against the current lists; a missing code remains visibly unresolved, without choosing another company, until you select a listed code or explicitly accept manual mode. A profile does not set the setup-ready flag. Account/environment/schema changes invalidate both setup lists and require a new Load setup. Refreshing setup clears prior data results. A LegalEntities response describes the rows visible to the signed-in user; it does not guarantee the user can read every data entity for every listed company.

### Data scope and correctness

Field selection belongs to each entity and is saved with its available-field schema descriptions in profile version 2. All-false flags remain all-false when loading a profile. Older profiles without exclusions migrate to full mode; older profiles with exclusions migrate to an equivalent explicit selection, preserving their former download scope. The current schemas remain authoritative; stale or duplicate field names are rejected. Loading a legacy profile does not rewrite it until **Save profile**.

In explicit mode, exclusions override optional checked fields; excluded matching-key fields remain an error. If every checked field is excluded, the plan is blocked instead of silently switching to full mode. Key/company fields are still included when needed even without checkboxes. Full mode can receive excluded fields from FO, but those values are not canonicalized, spooled or exported. This distinction is confirmed in the pre-run dialog.

For wide entities such as CustomersV3, checking every field can create a very long `$select` and encounter server/proxy query-length limits. **Clear Selected** avoids that projection entirely for a full comparison; **Select All** deliberately retains an explicit projection. This optimization does not bypass company filters, permissions, paging limits or diagnostics.

Company-scoped requests use an explicit company equality filter plus `cross-company=true`; they never rely on the user's default company. Different Source/Target companies are an explicit one-to-one mapping: their company field is normalized to `$company` for key/value matching, with original codes preserved in details. Returned records outside the configured company are rejected. Shared entities have no company filter. The tool does not discover company scope from field names.

The default matching key must have the same ordered names on both sides; a business-key override must be explicit and is checked for missing/null/invalid values and duplicates. `Missing`, `Null`, empty string, false and zero remain distinct. Int64 and decimal values retain precision (decimal canonicalization uses coefficient/scale rather than double); string equality is ordinal. DateTimeOffset compares explicit instants in UTC, while unsupported temporal precision is rejected instead of silently truncated. Enum definitions must be compatible. Computed or volatile fields are not automatically excluded.

Results are `Equal`, `Different`, `OnlySource`, `OnlyTarget`, `DuplicateKey`, `InvalidKey` or `Inconclusive`. Only two fully read datasets produce definitive missing-record classifications. A failed, canceled or limited entity is not published as conclusive data results. Inconclusive keys and skipped entities prevent the run from claiming complete equivalence. Equal means the chosen fields and authorized scope match, not that the databases are identical. Each entity records its capture interval; reads across two environments are not a single transaction and concurrent business changes can affect results. Zero rows cannot prove company validity or unrestricted access.

### Failure diagnostics

When an entity fails, select its row in **Data results** and expand **Error diagnostics**. The selectable detail includes the Source/Target side, operation phase, page number, original exception type, filtered reason, HTTP status and request/correlation ID when available. JSON errors also report the parse path and position. Server error codes/messages are read from a bounded OData error response; the full HTTP body, request URL, authorization headers and record values are not retained as diagnostics. Quoted values and known filter literals are redacted while known schema identifiers can remain visible. No new HTTP logging is enabled.

These details are included in an explicitly requested report. A failure before committing the first page can legitimately show zero record counters even when FO returned a response: the diagnostic phase distinguishes HTTP, parsing, company validation, nextLink validation and local page persistence. The diagnostics do not change retry limits, query scope or comparison rules.

### Data limits and protection

Initial editable limits are 100,000 rows per side/entity, 1,000 pages per side/entity, 2,048 MiB temporary disk per job and 100 entities per run. Each HTTP page is additionally bounded to 32 MiB; server-driven pagination is followed without using `$top` to truncate datasets. The data client has no short HTTP timeout, uses the same FO audience, disables redirects and request logging, and limits retries to two for transient failures before the body is committed. HTTP 401/403 or identity changes stop the job. Next links must remain on the same HTTPS EntitySet, and automatic navigation expansion is forbidden.

Data is buffered by page, then stored in a per-run SQLite database beneath `%LOCALAPPDATA%/D365FODataComparisonTool/DataRuns/Temp`. Record payloads and canonical keys use authenticated AES-GCM encryption with a per-run random key protected by DPAPI for the current Windows user; index keys use a separate random per-run HMAC secret. Neither records, keys nor query strings are written to application logs. Duplicate groups retain multiple rows. Matching and result pagination run off the UI thread, not in an in-memory copy of all records.

**Clear data results**, a new run, account/environment/schema changes and app closure dispose the temporary store. Orphaned application-owned runs are cleaned on a subsequent data run without deleting another active instance's files. Secure forensic deletion on SSDs is not guaranteed. Profiles are separately DPAPI-protected beneath `DataProfiles`, bound to the account/environment/schema pair and contain configuration/filters, not downloaded records. A changed schema pair does not silently reuse the previous profile.

Exports are explicit unencrypted files at the path you approve. CSV uses quoting and formula-injection protection; JSON preserves value states and originals. Both include the comparison scope, fingerprints, capture times, failures and completeness. Cancellation removes partial export files. Do not retain or share exported business data outside your organization's policy.

### Data POC

Authorization for connection checks or metadata is not authorization to read business records. Before a live data POC, have the environment owner approve the exact entities, fields, company pair, filters, record limits and export location. Start in sandbox with a small known dataset containing equal, changed and missing records. Validate permissions, business-key uniqueness and paging before increasing volume. PROD requires separate approval. Automated tests use synthetic records only; no real records are committed as fixtures.

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