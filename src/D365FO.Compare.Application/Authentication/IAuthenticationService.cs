using D365FO.Compare.Domain.Authentication;

namespace D365FO.Compare.Application.Authentication;

public interface IAuthenticationService
{
    Task<UserSession> SignInAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken, string? preferredHomeAccountId = null);
    Task<UserSession> ChangeAccountAsync(string tenantId, string clientId, IntPtr parentWindow, CancellationToken cancellationToken);
    Task<string> GetAccessTokenAsync(string tenantId, IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken);
    Task SignOutAsync(string tenantId, string clientId, CancellationToken cancellationToken);
    Task<UserSession?> GetCurrentSessionAsync(string tenantId, string clientId, CancellationToken cancellationToken, string? preferredHomeAccountId = null);
    Task<UserSession?> GetCurrentSessionAsync(string tenantId, CancellationToken cancellationToken, string? preferredHomeAccountId = null);
}