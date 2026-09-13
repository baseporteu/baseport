namespace Baseport;

public readonly record struct ApiProblem(int Status, string Type, string Title)
{
    public static readonly ApiProblem BadRequest = new(400, Urn("bad-request"), "Malformed request");
    public static readonly ApiProblem Unauthorized = new(401, Urn("unauthorized"), "Missing or invalid bearer token");
    public static readonly ApiProblem Forbidden = new(403, Urn("forbidden"), "Not permitted");
    public static readonly ApiProblem NotFound = new(404, Urn("not-found"), "Not found");
    public static readonly ApiProblem MethodNotAllowed = new(405, Urn("method-not-allowed"), "Method not enabled");
    public static readonly ApiProblem NotAcceptable = new(406, Urn("not-acceptable"), "No representation matches the Accept header");
    public static readonly ApiProblem Conflict = new(409, Urn("conflict"), "Conflicts with stored data");
    public static readonly ApiProblem PreconditionFailed = new(412, Urn("precondition-failed"), "The record changed since the version you hold");
    public static readonly ApiProblem TooLarge = new(413, Urn("content-too-large"), "Request body is too large");
    public static readonly ApiProblem UnsupportedMediaType = new(415, Urn("unsupported-media-type"), "Unsupported content type");
    public static readonly ApiProblem Unprocessable = new(422, Urn("validation-failed"), "Validation failed");
    public static readonly ApiProblem TooManyRequests = new(429, Urn("too-many-requests"), "Rate limit exceeded");
    public static readonly ApiProblem Internal = new(500, Urn("internal-error"), "Internal server error");
    public static readonly ApiProblem BadGateway = new(502, Urn("bad-gateway"), "Invalid response from an upstream service");
    public static readonly ApiProblem GatewayTimeout = new(504, Urn("gateway-timeout"), "An upstream service timed out");

    public static readonly IReadOnlyList<ApiProblem> All =
    [
        BadRequest, Unauthorized, Forbidden, NotFound, MethodNotAllowed, NotAcceptable, Conflict,
        PreconditionFailed, TooLarge, UnsupportedMediaType, Unprocessable, TooManyRequests, Internal, BadGateway, GatewayTimeout
    ];

    private static string Urn(string slug) => $"urn:baseport:problem:{slug}";
}

public static class ApiProblems
{
    public const string ContentType = "application/problem+json";

    public static IResult Write(
        HttpContext ctx,
        ApiProblem problem,
        string detail,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? invalid = null) =>
        Results.Json(Body(ctx, problem, detail, errors, invalid), statusCode: problem.Status, contentType: ContentType);

    public static Dictionary<string, object?> Body(
        HttpContext ctx,
        ApiProblem problem,
        string detail,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? invalid = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = problem.Type,
            ["title"] = problem.Title,
            ["status"] = problem.Status,
            ["detail"] = detail,
            ["instance"] = ctx.Request.Path.Value ?? "",
            ["errors"] = errors is { Count: > 0 } ? errors : [detail]
        };
        if (invalid is { Count: > 0 }) body["invalid"] = invalid;
        return body;
    }

    public static IResult Write(
        HttpContext ctx,
        ApiProblem problem,
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? invalid = null) =>
        Write(ctx, problem, errors.Count > 0 ? errors[0] : problem.Title, errors, invalid);

    public static IResult FromOutcome(HttpContext ctx, RecordEngine.ValidationOutcome outcome) =>
        Write(
            ctx,
            outcome.Failure is ValidationFailure.Conflict ? ApiProblem.Conflict : ApiProblem.Unprocessable,
            outcome.Errors,
            outcome.InvalidFields);
}
