using System.Diagnostics;
using System;
using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Core.Localization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace FSH.Framework.Web.Exceptions;

public class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IStringLocalizer<SharedResources> localizer,
    IStringLocalizerFactory localizerFactory) : IExceptionHandler
{
    private static string TitleKeyFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => "Error.NotFound",
        HttpStatusCode.Unauthorized => "Error.Unauthorized",
        HttpStatusCode.Forbidden => "Error.Forbidden",
        HttpStatusCode.BadRequest => "Error.BadRequest",
        _ => "Error.Unexpected",
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var problemDetails = new ProblemDetails
        {
            Instance = httpContext.Request.Path
        };

        var statusCode = StatusCodes.Status500InternalServerError;

        if (exception is FluentValidation.ValidationException fluentException)
        {
            statusCode = StatusCodes.Status400BadRequest;

            problemDetails.Status = statusCode;
            problemDetails.Title = localizer["Error.Validation"];
            problemDetails.Detail = localizer["Error.Validation.Detail"];
            problemDetails.Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1";

            var errors = fluentException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            problemDetails.Extensions["errors"] = errors;
        }
        else if (exception is CustomException e)
        {
            statusCode = (int)e.StatusCode;
            problemDetails.Status = statusCode;

            var title = localizer[TitleKeyFor(e.StatusCode)];
            problemDetails.Title = title.ResourceNotFound ? e.GetType().Name : title.Value;

            if (e.MessageKey is not null)
            {
                var moduleLocalizer = localizerFactory.Create(e.ResourceSource ?? typeof(SharedResources));
                try
                {
                    // The [key, args] indexer always runs string.Format; a literal '{' or an orphan
                    // '{1}' in the resx would throw FormatException from inside the handler. Use the
                    // single-arg indexer when there are no args, and fall back to the English message.
                    var message = e.MessageArgs.Count == 0
                        ? moduleLocalizer[e.MessageKey]
                        : moduleLocalizer[e.MessageKey, e.MessageArgs.ToArray()];
                    problemDetails.Detail = message.ResourceNotFound ? e.Message : message.Value;
                }
                catch (FormatException)
                {
                    problemDetails.Detail = e.Message;
                }
            }
            else
            {
                problemDetails.Detail = e.Message;
            }

            if (e.ErrorMessages is { Count: > 0 })
            {
                problemDetails.Extensions["errors"] = e.ErrorMessages;
            }
        }
        else if (exception is UnauthorizedAccessException)
        {
            statusCode = StatusCodes.Status401Unauthorized;
            problemDetails.Status = statusCode;
            problemDetails.Title = localizer["Error.Unauthorized"];
            problemDetails.Detail = exception.Message;
        }
        else if (exception is KeyNotFoundException)
        {
            statusCode = StatusCodes.Status404NotFound;
            problemDetails.Status = statusCode;
            problemDetails.Title = localizer["Error.NotFound"];
            problemDetails.Detail = exception.Message;
        }
        else if (exception is BadHttpRequestException badRequest)
        {
            // BadHttpRequestException = malformed request (missing required header/param, unreadable/oversized body).
            // Client error carrying the correct status (usually 400) — honour it instead of falling through to a generic 500.
            statusCode = badRequest.StatusCode;
            problemDetails.Status = statusCode;
            problemDetails.Title = localizer["Error.BadRequest"];
            problemDetails.Detail = badRequest.Message;
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            problemDetails.Status = statusCode;
            problemDetails.Title = localizer["Error.Unexpected"];
            problemDetails.Detail = localizer["Error.Unexpected.Detail"];
        }

        httpContext.Response.StatusCode = statusCode;

        // Surface trace and correlation IDs so clients/support can correlate errors to traces
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["traceId"] = traceId;

        var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["correlationId"] = correlationId;

        // Log the raw (English) exception message and type, never the localized ProblemDetails body,
        // so log entries stay culture-independent regardless of the request's negotiated culture.
        // PushProperty returns an IDisposable that pops the property on dispose; scope it to the LogError
        // call so it does not leak onto every subsequent log entry of the request (AsyncLocal contamination).
        var logPath = httpContext.Request.Path.Value?.Replace(Environment.NewLine, string.Empty);
        using (LogContext.PushProperty("exception_type", exception.GetType().Name))
        using (LogContext.PushProperty("exception_detail", exception.Message))
        using (LogContext.PushProperty("exception_statusCode", statusCode))
        using (LogContext.PushProperty("exception_stackTrace", exception.StackTrace))
        {
            logger.LogError("Exception at {Path} - {StatusCode} {Type}", logPath, statusCode, exception.GetType().Name);
        }

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken).ConfigureAwait(false);
        return true;
    }
}