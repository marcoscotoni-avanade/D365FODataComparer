using D365FO.Compare.Domain.Authentication;

namespace D365FO.Compare.Application.Authentication;

public interface IAuthenticationService
{
    Task<UserSession> SignInAsync(IntPtr parentWindow, CancellationToken cancellationToken);
    Task<UserSession> ChangeAccountAsync(IntPtr parentWindow, CancellationToken cancellationToken);
    Task<string> GetAccessTokenAsync(IReadOnlyCollection<string> scopes, IntPtr parentWindow, CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
    Task<UserSession?> GetCurrentSessionAsync(CancellationToken cancellationToken);
}