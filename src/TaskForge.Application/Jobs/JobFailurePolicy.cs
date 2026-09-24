namespace TaskForge.Application.Jobs;

public static class JobFailurePolicy
{
    public static bool IsPermanent(string normalizedErrorCode) =>
        normalizedErrorCode is "invalidpayload" or "unsupportedjobtype" or "nonretryablejobexception";
}
