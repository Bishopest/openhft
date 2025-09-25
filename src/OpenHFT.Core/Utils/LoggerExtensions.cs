using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace OpenHFT.Core.Utils;

public static class LoggerExtensions
{
    public static void LogInformationWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        logger.LogInformation(
            "[{MemberName}:{LineNumber}] {Message}",
            memberName, sourceLineNumber, message
        );
    }

    public static void LogWarningWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        logger.LogWarning(
            "[{MemberName}:{LineNumber}] {Message}",
            memberName, sourceLineNumber, message
        );
    }

    public static void LogErrorWithCaller(
        this ILogger logger,
        Exception exception,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        logger.LogError(
            exception,
            "[{MemberName}:{LineNumber}] {Message}",
            memberName, sourceLineNumber, message
        );
    }
}

