using System.Security.Cryptography;
using System.Collections.Concurrent;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Domain.Authentication;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace D365FO.Compare.Infrastructure.Authentication;

public sealed class MsalAuthenticationService : IAuthenticationService
{
    private readonly string? _defaultClientId;
    private readonly string[] _powerPlatformScopes;
    private readonly ILogger<MsalAuthenticationService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<IPublicClientApplication>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _selectedHomeAccountIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _clientIdsByTenant = new(StringComparer.OrdinalIgnoreCase);

    public MsalAuthenticationService(IConfiguration configuration, ILogger<MsalAuthenticationService> logger)
    {
        _defaultClientId = configuration["Authentication:ClientId"];
        _logger = logger;
        _powerPlatformScopes =
        [
            configuration["PowerPlatform:Scope"] ?? "https://api.powerplatform.com/.default"
        ];
    }

    public async Task<UserSession> SignInAsync(
        string tenantId,
        string clientId,
        IntPtr parentWindow,
        CancellationToken cancellationToken,
        string? preferredHomeAccountId = null)
    {
        tenantId = NormalizeTenantId(tenantId);
        clientId = NormalizeClientId(clientId);
        _clientIdsByTenant[tenantId] = clientId;
        var client = GetClient(tenantId, clientId);
        var accountKey = GetAccountKey(tenantId, clientId);
        var accounts = (await client.GetAccountsAsync().ConfigureAwait(false)).ToList();
        if (preferredHomeAccountId is not null)
        {
            _selectedHomeAccountIds[accountKey] = preferredHomeAccountId;
        }

        var hasSelectedAccount = _selectedHomeAccountIds.ContainsKey(accountKey);
        var account = hasSelectedAccount
            ? FindSelectedAccount(accountKey, accounts)
            : accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;

        if (account is not null)
        {
            try
            {
                var result = await client
                    .AcquireTokenSilent(_powerPlatformScopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Remember(tenantId, clientId, result);
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        var request = client
            .AcquireTokenInteractive(_powerPlatformScopes)
            .WithParentActivityOrWindow(parentWindow);
        if (preferredHomeAccountId is not null && account is not null)
        {
            request = request.WithAccount(account);
        }
        else
        {
            request = request.WithPrompt(Prompt.SelectAccount);
        }

        var interactiveResult = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var session = Remember(tenantId, clientId, interactiveResult);
        if (preferredHomeAccountId is not null
            && !string.Equals(preferredHomeAccountId, session.HomeAccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Microsoft sign-in returned a different account than the one saved in this profile. Use Change account to update the profile explicitly.");
        }

        return session;
    }

    public async Task<UserSession> ChangeAccountAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken)
    {
        tenantId = NormalizeTenantId(tenantId);
        clientId = NormalizeClientId(clientId);
        _clientIdsByTenant[tenantId] = clientId;
        var accountKey = GetAccountKey(tenantId, clientId);
        var client = GetClient(tenantId, clientId);
        foreach (var account in await client.GetAccountsAsync().ConfigureAwait(false))
        {
            await client.RemoveAsync(account).ConfigureAwait(false);
        }

        _selectedHomeAccountIds.TryRemove(accountKey, out _);
        var result = await client
            .AcquireTokenInteractive(_powerPlatformScopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithParentActivityOrWindow(parentWindow)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return Remember(tenantId, clientId, result);
    }

    public async Task<string> GetAccessTokenAsync(
        string tenantId,
        IReadOnlyCollection<string> scopes,
        IntPtr parentWindow,
        CancellationToken cancellationToken)
    {
        tenantId = NormalizeTenantId(tenantId);
        var clientId = GetClientIdForTenant(tenantId);
        var accountKey = GetAccountKey(tenantId, clientId);
        var client = GetClient(tenantId, clientId);
        var accounts = (await client.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var selectedAccountExists = _selectedHomeAccountIds.ContainsKey(accountKey);
        var account = FindSelectedAccount(accountKey, accounts) ?? (selectedAccountExists ? null : accounts.FirstOrDefault());

        if (account is null)
        {
            throw new InvalidOperationException("The selected account is no longer available for this tenant. Sign in again from the Account tab.");
        }

        try
        {
            var result = await client
                .AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            Remember(tenantId, clientId, result);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var result = await client
                .AcquireTokenInteractive(scopes)
                .WithAccount(account)
                .WithParentActivityOrWindow(parentWindow)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            Remember(tenantId, clientId, result);
            return result.AccessToken;
        }
    }

    public async Task SignOutAsync(string tenantId, string clientId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tenantId = NormalizeTenantId(tenantId);
        clientId = NormalizeClientId(clientId);
        var accountKey = GetAccountKey(tenantId, clientId);
        var client = GetClient(tenantId, clientId);
        foreach (var account in await client.GetAccountsAsync().ConfigureAwait(false))
        {
            await client.RemoveAsync(account).ConfigureAwait(false);
        }

        _selectedHomeAccountIds.TryRemove(accountKey, out _);
        _clientIdsByTenant.TryRemove(tenantId, out _);
    }

    public async Task<UserSession?> GetCurrentSessionAsync(
        string tenantId,
        string clientId,
        CancellationToken cancellationToken,
        string? preferredHomeAccountId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
        {
            return null;
        }

        tenantId = NormalizeTenantId(tenantId);
        clientId = NormalizeClientId(clientId);
        _clientIdsByTenant[tenantId] = clientId;
        var accountKey = GetAccountKey(tenantId, clientId);
        if (preferredHomeAccountId is not null)
        {
            _selectedHomeAccountIds[accountKey] = preferredHomeAccountId;
        }

        var accounts = (await GetClient(tenantId, clientId).GetAccountsAsync().ConfigureAwait(false)).ToList();
        var hasSelectedAccount = _selectedHomeAccountIds.ContainsKey(accountKey);
        var account = FindSelectedAccount(accountKey, accounts) ?? (hasSelectedAccount ? null : accounts.FirstOrDefault());

        if (account?.HomeAccountId is null)
        {
            return null;
        }

        _selectedHomeAccountIds[accountKey] = account.HomeAccountId.Identifier;
        return new UserSession(
            tenantId,
            account.Username ?? string.Empty,
            account.HomeAccountId.Identifier);
    }

    public Task<UserSession?> GetCurrentSessionAsync(
        string tenantId,
        CancellationToken cancellationToken,
        string? preferredHomeAccountId = null) =>
        GetCurrentSessionAsync(tenantId, GetClientIdForTenant(tenantId), cancellationToken, preferredHomeAccountId);

    private UserSession Remember(string tenantId, string clientId, AuthenticationResult result)
    {
        var account = result.Account ?? throw new InvalidOperationException("Microsoft sign-in did not return an account.");
        var homeAccountId = account.HomeAccountId?.Identifier
            ?? throw new InvalidOperationException("Microsoft sign-in did not return a home account ID.");

        _selectedHomeAccountIds[GetAccountKey(tenantId, clientId)] = homeAccountId;
        return new UserSession(tenantId, account.Username ?? string.Empty, homeAccountId);
    }

    private IAccount? FindSelectedAccount(string accountKey, IReadOnlyList<IAccount> accounts)
    {
        if (!_selectedHomeAccountIds.TryGetValue(accountKey, out var selectedHomeAccountId))
        {
            return null;
        }

        return accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.HomeAccountId?.Identifier, selectedHomeAccountId, StringComparison.Ordinal));
    }

    private IPublicClientApplication GetClient(string tenantId, string clientId)
    {
        tenantId = NormalizeTenantId(tenantId);
        clientId = NormalizeClientId(clientId);
        var key = GetAccountKey(tenantId, clientId);

        return _clients.GetOrAdd(key, _ => new Lazy<IPublicClientApplication>(() =>
        {
            var client = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .Build();

            new ProtectedFileTokenCache(client.UserTokenCache, _logger, tenantId, clientId).Register();
            return client;
        })).Value;
    }

    private string GetClientIdForTenant(string tenantId)
    {
        tenantId = NormalizeTenantId(tenantId);
        if (_clientIdsByTenant.TryGetValue(tenantId, out var clientId))
        {
            return clientId;
        }

        if (_defaultClientId is not null && Guid.TryParse(_defaultClientId, out var defaultClientId))
        {
            clientId = defaultClientId.ToString("D");
            _clientIdsByTenant[tenantId] = clientId;
            return clientId;
        }

        throw new InvalidOperationException("No Client ID is active for this tenant. Activate a profile with a valid Application (Client) ID first.");
    }

    private static string GetAccountKey(string tenantId, string clientId) => $"{tenantId}|{clientId}";

    private static string NormalizeTenantId(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out var parsedTenantId))
        {
            throw new InvalidOperationException("The selected connection profile does not contain a valid tenant ID.");
        }

        return parsedTenantId.ToString("D");
    }

    private static string NormalizeClientId(string clientId)
    {
        if (!Guid.TryParse(clientId, out var parsedClientId))
        {
            throw new InvalidOperationException("The selected connection profile does not contain a valid Application (Client) ID.");
        }

        return parsedClientId.ToString("D");
    }

    private sealed class ProtectedFileTokenCache
    {
        private readonly ITokenCache _tokenCache;
        private readonly ILogger _logger;
        private readonly string _cachePath;
        private readonly object _sync = new();

        public ProtectedFileTokenCache(ITokenCache tokenCache, ILogger logger, string tenantId, string clientId)
        {
            _tokenCache = tokenCache;
            _logger = logger;
            _cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "D365FODataComparisonTool",
                "Auth",
                $"{tenantId}-{clientId}.msal.cache");
        }

        public void Register()
        {
            _tokenCache.SetBeforeAccess(OnBeforeAccess);
            _tokenCache.SetAfterAccess(OnAfterAccess);
        }

        private void OnBeforeAccess(TokenCacheNotificationArgs args)
        {
            lock (_sync)
            {
                if (!File.Exists(_cachePath))
                {
                    return;
                }

                try
                {
                    var protectedBytes = File.ReadAllBytes(_cachePath);
                    var cacheBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    args.TokenCache.DeserializeMsalV3(cacheBytes);
                }
                catch (CryptographicException exception)
                {
                    _logger.LogWarning(exception, "Discarding an unreadable encrypted authentication cache.");
                    File.Delete(_cachePath);
                }
            }
        }

        private void OnAfterAccess(TokenCacheNotificationArgs args)
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            lock (_sync)
            {
                var directory = Path.GetDirectoryName(_cachePath)!;
                Directory.CreateDirectory(directory);

                var cacheBytes = args.TokenCache.SerializeMsalV3();
                var protectedBytes = ProtectedData.Protect(cacheBytes, null, DataProtectionScope.CurrentUser);
                var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";

                try
                {
                    File.WriteAllBytes(temporaryPath, protectedBytes);
                    File.Move(temporaryPath, _cachePath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }
    }
}