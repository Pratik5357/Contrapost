using Contrapost.Domain;

namespace Contrapost.Api.Http;

/// <summary>Maps <see cref="LedgerError"/> to RFC 9457 Problem Details.</summary>
internal static class LedgerProblems
{
    public static IResult ToProblem(this LedgerError error) => error.Kind switch
    {
        ErrorKind.Validation => TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(error.Errors),
            error.Detail,
            title: error.Title,
            type: TypeUri(error.Code)),
        _ => TypedResults.Problem(
            error.Detail,
            statusCode: StatusOf(error.Kind),
            title: error.Title,
            type: TypeUri(error.Code),
            extensions: new Dictionary<string, object?>(error.Extensions)),
    };

    public static IResult NotFound(string code, string title, string detail) =>
        new LedgerError(ErrorKind.NotFound, code, title, detail).ToProblem();

    private static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Rejected => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TypeUri(string code) => $"https://contrapost.dev/errors/{code}";
}
