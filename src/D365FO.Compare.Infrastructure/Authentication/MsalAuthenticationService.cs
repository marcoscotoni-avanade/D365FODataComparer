using System.Security.Cryptography;
using D365FO.Compare.Application.Authentication;
using D365FO.Compare.Domain.Authentication;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace D365FO.Compare.Infrastructure.Authentication;

public sealed class MsalAuthenticationService : IAuthenticationService
{
    private readonly string? _clientId;
    private readonly string? _tenantId;
    private readonly Lazy<IPublicClientApplication> _client;
    private readonly string[] _powerPlatformScopes;
    private readonly ILogger<MsalAuthenticationService> _logger;
    private string? _selectedHomeAccountId;

    public MsalAuthenticationService(IConfiguration configuration, ILogger<MsalAuthenticationService> logger)
    {
        _clientId = configuration["Authentication:ClientId"];
        _tenantId = configuration["Authentication:TenantId"];
        _logger = logger;
        _powerPlatformScopes =
        [
            configuration["PowerPlatform:Scope"] ?? "https://api.powerplatform.com/.default"
        ];

        _client = new Lazy<IPublicClientApplication>(CreateClient);
    }

    public async Task<UserSession> SignInAsync(IntPtr parentWindow, CancellationToken cancellationToken)
    {
        var client = Client;
        var accounts = (await client.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var account = accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;

        try
        {
            var result = await client
                .AcquireTokenSilent(_powerPlatformScopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return Remember(result);
        }
        catch (MsalUiRequiredException)
        {
            var request = client
                .AcquireTokenInteractive(_powerPlatformScopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithParentActivityOrWindow(parentWindow);

            var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return Remember(result);
        }
    }

    public async Task<UserSession> ChangeAccountAsync(IntPtr parentWindow, CancellationToken cancellationToken)
    {
        var client = Client;
        foreach (var account in await client.GetAccountsAsync().ConfigureAwait(false))
        {
            await client.RemoveAsync(account).ConfigureAwait(false);
        }

        _selectedHomeAccountId = null;
        var result = await client
            .AcquireTokenInteractive(_powerPlatformScopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithParentActivityOrWindow(parentWindow)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return Remember(result);
    }

    public async Task<string> GetAccessTokenAsync(
        IReadOnlyCollection<string> scopes,
        IntPtr parentWindow,
        CancellationToken cancellationToken)
    {
        var client = Client;
        var accounts = (await client.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var account = accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.HomeAccountId?.Identifier, _selectedHomeAccountId, StringComparison.Ordinal));

        if (account is null)
        {
            throw new InvalidOperationException("No signed-in account is available. Sign in from the Account tab before testing the connection.");
        }

        try
        {
            var result = await client
                .AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            Remember(result);
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

            Remember(result);
            return result.AccessToken;
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var account in await Client.GetAccountsAsync().ConfigureAwait(false))
        {
            await Client.RemoveAsync(account).ConfigureAwait(false);
        }

        _selectedHomeAccountId = null;
    }

    public async Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(_clientId, out _) || !Guid.TryParse(_tenantId, out _))
        {
            return null;
        }

        var accounts = (await Client.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var account = accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.HomeAccountId?.Identifier, _selectedHomeAccountId, StringComparison.Ordinal))
            ?? accounts.FirstOrDefault();

        if (account?.HomeAccountId is null)
        {
            return null;
        }

        _selectedHomeAccountId = account.HomeAccountId.Identifier;
        return new UserSession(
            _tenantId!,
            account.Username ?? string.Empty,
            account.HomeAccountId.Identifier);
    }

    private UserSession Remember(AuthenticationResult result)
    {
        var account = result.Account ?? throw new InvalidOperationException("Microsoft sign-in did not return an account.");
        var homeAccountId = account.HomeAccountId?.Identifier
            ?? throw new InvalidOperationException("Microsoft sign-in did not return a home account ID.");

        _selectedHomeAccountId = homeAccountId;
        return new UserSession(result.TenantId, account.Username ?? string.Empty, homeAccountId);
    }

    private IPublicClientApplication Client => _client.Value;

    private IPublicClientApplication CreateClient()
    {
        if (!Guid.TryParse(_clientId, out _) || !Guid.TryParse(_tenantId, out _))
        {
            throw new InvalidOperationException(
                "Configure Authentication:ClientId and Authentication:TenantId in appsettings.json before signing in.");
        }

        var client = PublicClientApplicationBuilder
            .Create(_clientId!)
            .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();

        new ProtectedFileTokenCache(client.UserTokenCache, _logger).Register();
        return client;
    }

    private sealed class ProtectedFileTokenCache
    {
        private readonly ITokenCache _tokenCache;
        private readonly ILogger _logger;
        private readonly string _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D365FODataComparisonTool",
            "Auth",
            "msal.cache");
        private readonly object _sync = new();

        public ProtectedFileTokenCache(ITokenCache tokenCache, ILogger logger)
        {
            _tokenCache = tokenCache;
            _logger = logger;
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