using Microsoft.AspNetCore.Diagnostics;
using Monday.Application.Projects;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Api.Endpoints;

/// <summary>Turns domain and application errors into problem responses with a stable code.</summary>
public sealed class ErrorHandling(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var (status, code, title) = exception switch
        {
            ProductionReadOnlyException e => (StatusCodes.Status403Forbidden, e.Code, e.Message),
            AccessDeniedException e => (StatusCodes.Status403Forbidden, e.Code, "You don't have permission to do that on this project."),
            DomainException e when e.Code.StartsWith("access.", StringComparison.Ordinal) => (StatusCodes.Status403Forbidden, e.Code, e.Message),
            DomainException e when e.Code.EndsWith(".not_found", StringComparison.Ordinal) => (StatusCodes.Status404NotFound, e.Code, e.Message),
            DomainException e => (StatusCodes.Status400BadRequest, e.Code, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, $"{e.What}.not_found", e.Message),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }
}
