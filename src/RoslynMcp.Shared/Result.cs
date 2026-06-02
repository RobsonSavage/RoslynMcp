namespace RoslynMcp.Shared;

public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly ErrorResponse? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(ErrorResponse error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the success value. Returns <c>default(T)</c> when <see cref="IsSuccess"/> is <c>false</c>.
    /// Callers should always check <see cref="IsSuccess"/> before accessing this property.
    /// </summary>
    public T? Value
    {
        get
        {
#if DEBUG
            if (!IsSuccess)
                throw new InvalidOperationException("Cannot access Value on a failed Result. Check IsSuccess first.");
#endif
            return _value;
        }
    }

    /// <summary>
    /// Gets the error details. Returns <c>null</c> when <see cref="IsSuccess"/> is <c>true</c>.
    /// Callers should always check <see cref="IsSuccess"/> before accessing this property.
    /// </summary>
    public ErrorResponse? Error => _error;

    public static Result<T> Ok(T value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value), "Cannot create a success Result with a null value");
        return new(value);
    }

    public static Result<T> Fail(string message, string? errorCode = null) => new(new ErrorResponse(message, errorCode));
    public static Result<T> Fail(ErrorResponse error) => new(error);

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(ErrorResponse error) => Fail(error);
}
