using System.Diagnostics.CodeAnalysis;

namespace Contrapost.Domain;

public enum ErrorKind
{
    /// <summary>The request is malformed; nothing was looked up. HTTP 400.</summary>
    Validation,

    /// <summary>HTTP 404. Also used for resources owned by another client, so they can't be probed.</summary>
    NotFound,

    /// <summary>The request clashes with existing state, e.g. a duplicate reference. HTTP 409.</summary>
    Conflict,

    /// <summary>A business rule rejected an otherwise valid request, e.g. insufficient funds. HTTP 422.</summary>
    Rejected,
}

/// <summary>
/// An expected failure. <see cref="Code"/> is stable and becomes the Problem Details type
/// (https://contrapost.dev/errors/{code}).
/// </summary>
public sealed record LedgerError(ErrorKind Kind, string Code, string Title, string Detail)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public IReadOnlyDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();

    public static LedgerError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ErrorKind.Validation, "validation-failed", "The request is invalid", "One or more fields are invalid.")
        {
            Errors = errors,
        };
}

/// <summary>Either a value or a <see cref="LedgerError"/>.</summary>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Success/Failure factories are the idiomatic way to build a result.")]
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, LedgerError? error)
    {
        _value = value;
        Error = error;
    }

    public LedgerError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {Error!.Code}");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(LedgerError error) => new(default, error);

#pragma warning disable CA2225 // implicit conversions keep call sites readable; Success/Failure are the named alternates
    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(LedgerError error) => Failure(error);
#pragma warning restore CA2225
}
