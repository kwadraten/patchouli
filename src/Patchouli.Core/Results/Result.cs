using Patchouli.Core.Conflicts;

namespace Patchouli.Core.Results;

public interface IOperationOutcome
{
    bool IsSuccess { get; }
    bool IsCancelled => false;
    string? ErrorMessage { get; }
}

public interface IResultFailureDetails
{
    string Kind { get; }
}

public sealed record MappingRequiredDetails(
    string RootKind,
    string LogicalRootId,
    string LibraryId,
    string DeviceId,
    string RecoveryAction) : IResultFailureDetails
{
    public string Kind => "mapping_required";
}

public sealed record Result : IOperationOutcome
{
    private Result(
        bool isSuccess,
        string? errorCode,
        string? errorMessage,
        IReadOnlyList<ConflictDescriptor> conflicts,
        IResultFailureDetails? details)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Conflicts = conflicts;
        Details = details;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<ConflictDescriptor> Conflicts { get; }
    public IResultFailureDetails? Details { get; }

    public static Result Success()
    {
        return new Result(true, null, null, Array.Empty<ConflictDescriptor>(), null);
    }

    public static Result Failure(
        string errorCode,
        string errorMessage,
        IReadOnlyList<ConflictDescriptor>? conflicts = null,
        IResultFailureDetails? details = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code is required.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Error message is required.", nameof(errorMessage));
        }

        return new Result(false, errorCode, errorMessage, conflicts ?? Array.Empty<ConflictDescriptor>(), details);
    }
}

public sealed record Result<T> : IOperationOutcome
{
    private readonly T? _value;

    private Result(
        bool isSuccess,
        T? value,
        string? errorCode,
        string? errorMessage,
        IReadOnlyList<ConflictDescriptor> conflicts,
        IResultFailureDetails? details)
    {
        IsSuccess = isSuccess;
        _value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Conflicts = conflicts;
        Details = details;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value
    {
        get
        {
            if (IsFailure)
            {
                throw new InvalidOperationException("Cannot access Value for a failed result.");
            }

            return _value!;
        }
    }

    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<ConflictDescriptor> Conflicts { get; }
    public IResultFailureDetails? Details { get; }

    public static Result<T> Success(T value)
    {
        return new Result<T>(true, value, null, null, Array.Empty<ConflictDescriptor>(), null);
    }

    public static Result<T> Failure(
        string errorCode,
        string errorMessage,
        IReadOnlyList<ConflictDescriptor>? conflicts = null,
        IResultFailureDetails? details = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code is required.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Error message is required.", nameof(errorMessage));
        }

        return new Result<T>(false, default, errorCode, errorMessage, conflicts ?? Array.Empty<ConflictDescriptor>(),
            details);
    }
}
