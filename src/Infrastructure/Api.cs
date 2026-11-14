using Microsoft.AspNetCore.Http;

namespace OpenForge.Infrastructure;

public static class Api
{
    public static IResult Json(object value, int status = 200)
    {
        return Results.Json(value, JsonBody.Options, statusCode: status);
    }

    public static IResult Ok()
    {
        return Json(new { ok = true });
    }
}
