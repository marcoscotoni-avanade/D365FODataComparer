using D365FO.Compare.Domain.Environments;

namespace D365FO.Compare.Application.Validation;

public interface IConnectionValidationService
{
    Task<ConnectionValidationResult> ValidateAsync(
        DiscoveredEnvironment environment,
        IntPtr parentWindow,
        CancellationToken cancellationToken);
}