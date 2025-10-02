using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace OpenHFT.Core.Utils;

public static class LoggerExtensions
{
    public static void LogInformationWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        [CallerFilePath] string sourceFilePath = "")
    {
        var className = Path.GetFileNameWithoutExtension(sourceFilePath);
        logger.LogInformation(
            "[{ClassName}.{MemberName}:{LineNumber}] {Message}",
            className,
            memberName,
            sourceLineNumber,
            message
        );
    }

    public static void LogWarningWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        [CallerFilePath] string sourceFilePath = "")
    {
        var className = Path.GetFileNameWithoutExtension(sourceFilePath);
        logger.LogInformation(
            "[{ClassName}.{MemberName}:{LineNumber}] {Message}",
            className, // 추출된 클래스 이름
            memberName,
            sourceLineNumber,
            message
        );
    }

    public static void LogErrorWithCaller(
        this ILogger logger,
        Exception exception,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        [CallerFilePath] string sourceFilePath = "")
    {
        var className = Path.GetFileNameWithoutExtension(sourceFilePath);
        logger.LogError(
            exception,
            "[{ClassName}.{MemberName}:{LineNumber}] {Message}",
            className,
            memberName,
            sourceLineNumber,
            message
        );
    }
}

