namespace D365FO.Compare.Application.Validation;

public interface IFinOpsHostPolicy
{
    bool Approve(Uri? finOpsUrl);
    bool IsAllowed(Uri? finOpsUrl);
}