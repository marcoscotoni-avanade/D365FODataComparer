namespace D365FO.Compare.Domain.Authentication;

public sealed record UserSession(
    string TenantId,
    string Username,
    string HomeAccountId);