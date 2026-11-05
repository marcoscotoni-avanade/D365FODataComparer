using System.Collections.Concurrent;
using D365FO.Compare.Application.Validation;

namespace D365FO.Compare.Infrastructure.Validation;

public sealed class ApprovedFinOpsHostPolicy : IFinOpsHostPolicy
{
    private readonly ConcurrentDictionary<string, byte> _approvedAuthorities = new(StringComparer.OrdinalIgnoreCase);

    public bool Approve(Uri? finOpsUrl)
    {
        var authority = GetApprovedAuthority(finOpsUrl);
        if (authority is null)
        {
            return false;
        }

        _approvedAuthorities.TryAdd(authority, 0);
        return true;
    }

    public bool IsAllowed(Uri? finOpsUrl)
    {
        var authority = GetApprovedAuthority(finOpsUrl);
        return authority is not null && _approvedAuthorities.ContainsKey(authority);
    }

    private static string? GetApprovedAuthority(Uri? finOpsUrl)
    {
        if (finOpsUrl is not { IsAbsoluteUri: true }
            || !string.Equals(finOpsUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !finOpsUrl.IsDefaultPort
            || !string.IsNullOrEmpty(finOpsUrl.UserInfo)
            || !string.IsNullOrEmpty(finOpsUrl.Query)
            || !string.IsNullOrEmpty(finOpsUrl.Fragment)
            || finOpsUrl.AbsolutePath != "/")
        {
            return null;
        }

        return finOpsUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}