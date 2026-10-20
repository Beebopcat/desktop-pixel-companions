namespace AzerothCompanion.Services;

/// <summary>
/// User authorization, separate from privacy consent and the historical spending ledger.
/// There is deliberately no UI, environment-variable, key, preference or command-line override.
/// A future change requires the user's explicit paid-service authorization first.
/// </summary>
public static class PaidServicePolicy
{
    public static bool IsAuthorized => false;
    public const decimal AuthorizedSpendingUsd = 0m;
    public const string Message = "Paid services are blocked. Authorized spending is $0. Evaluate local and free options first; explicit user authorization is required before enabling any paid service.";

    public static void RequireAuthorization()
    {
        if (!IsAuthorized) throw new CompanionServiceException(Message);
    }
}
