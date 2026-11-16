using System;

namespace OpenForge.Infrastructure;

public sealed class ApiException : Exception
{
    public ApiException(int status, string? message = null) : base(message ?? DefaultMessage(status))
    {
        Status = status;
    }

    public int Status { get; }

    public static ApiException BadRequest(string message)
    {
        return new ApiException(400, message);
    }

    public static ApiException Unauthorized(string message)
    {
        return new ApiException(401, message);
    }

    public static ApiException Forbidden(string message)
    {
        return new ApiException(403, message);
    }

    public static ApiException NotFound(string message)
    {
        return new ApiException(404, message);
    }

    public static ApiException Conflict(string message)
    {
        return new ApiException(409, message);
    }

    // Werkzeug's wording, which the v1 API returned when no message was given.
    public static string DefaultMessage(int status)
    {
        return status switch
        {
            400 => "The browser (or proxy) sent a request that this server could not understand.",
            401 => "The server could not verify that you are authorized to access the URL requested. You either supplied the wrong credentials (e.g. a bad password), or your browser doesn't understand how to supply the credentials required.",
            403 => "You don't have the permission to access the requested resource. It is either read-protected or not readable by the server.",
            404 => "The requested URL was not found on the server. If you entered the URL manually please check your spelling and try again.",
            405 => "The method is not allowed for the requested URL.",
            409 => "A conflict happened while processing the request. The resource might have been modified while the request was being processed.",
            413 => "The data value transmitted exceeds the capacity limit.",
            429 => "Too many requests. Wait a moment and try again.",
            _ => "The request could not be completed.",
        };
    }
}
