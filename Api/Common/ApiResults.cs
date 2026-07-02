namespace Api.Common;

/// 모든 응답을 일관 envelope { code, data } 로. 서버는 머신리더블 code 만 내보내고,
/// 표시 문구는 프론트가 locale 별로 매핑한다 (글로벌 서비스 다국어). 정상·오류 동일 형태.
public static class ApiResults
{
    public static IResult Ok(string code) =>
        Results.Ok(new ApiResponse<object?>(code, null));

    public static IResult Ok<T>(string code, T data) =>
        Results.Ok(new ApiResponse<T>(code, data));

    public static IResult Created<T>(string code, T data) =>
        Results.Json(new ApiResponse<T>(code, data), statusCode: StatusCodes.Status201Created);

    public static IResult BadRequest(string code) =>
        Error<object?>(StatusCodes.Status400BadRequest, code, null);

    public static IResult Unauthorized(string code) =>
        Error<object?>(StatusCodes.Status401Unauthorized, code, null);

    public static IResult NotFound(string code) =>
        Error<object?>(StatusCodes.Status404NotFound, code, null);

    public static IResult Forbidden(string code) =>
        Error<object?>(StatusCodes.Status403Forbidden, code, null);

    public static IResult Forbidden<T>(string code, T data) =>
        Error(StatusCodes.Status403Forbidden, code, data);

    public static IResult Conflict(string code) =>
        Error<object?>(StatusCodes.Status409Conflict, code, null);

    public static IResult ServiceUnavailable(string code) =>
        Error<object?>(StatusCodes.Status503ServiceUnavailable, code, null);

    public static IResult ServiceUnavailable<T>(string code, T data) =>
        Error(StatusCodes.Status503ServiceUnavailable, code, data);

    private static IResult Error<T>(int statusCode, string code, T data) =>
        Results.Json(new ApiResponse<T>(code, data), statusCode: statusCode);
}

public sealed record ApiResponse<T>(string Code, T Data);
