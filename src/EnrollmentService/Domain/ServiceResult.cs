namespace EnrollmentService.Domain;

public class ServiceResult
{
    public bool IsSuccess { get; }
    public int StatusCode { get; }
    public string? ErrorCode { get; }
    public string? ErrorTitle { get; }
    public string? ErrorDetail { get; }
    public IDictionary<string, object?>? Extensions { get; }

    protected ServiceResult(
        bool isSuccess,
        int statusCode,
        string? errorCode,
        string? errorTitle,
        string? errorDetail,
        IDictionary<string, object?>? extensions)
    {
        IsSuccess = isSuccess;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorTitle = errorTitle;
        ErrorDetail = errorDetail;
        Extensions = extensions;
    }

    public static ServiceResult Ok(int statusCode = 200) =>
        new(true, statusCode, null, null, null, null);

    public static ServiceResult Failure(
        int statusCode,
        string errorCode,
        string errorTitle,
        string errorDetail,
        IDictionary<string, object?>? extensions = null) =>
        new(false, statusCode, errorCode, errorTitle, errorDetail, extensions);
}

public sealed class ServiceResult<T> : ServiceResult
{
    public T? Value { get; }

    private ServiceResult(
        bool isSuccess,
        T? value,
        int statusCode,
        string? errorCode,
        string? errorTitle,
        string? errorDetail,
        IDictionary<string, object?>? extensions)
        : base(isSuccess, statusCode, errorCode, errorTitle, errorDetail, extensions)
    {
        Value = value;
    }

    public static ServiceResult<T> Ok(T value, int statusCode = 200) =>
        new(true, value, statusCode, null, null, null, null);

    public new static ServiceResult<T> Failure(
        int statusCode,
        string errorCode,
        string errorTitle,
        string errorDetail,
        IDictionary<string, object?>? extensions = null) =>
        new(false, default, statusCode, errorCode, errorTitle, errorDetail, extensions);
}
