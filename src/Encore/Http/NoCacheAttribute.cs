using Microsoft.AspNetCore.Mvc.Filters;

namespace Encore.Http
{
    /// <summary>
    /// Lidarr tells browsers to keep anything outside /api for a year, which would keep showing
    /// old video lists, old artwork and the old ui.js after an update. Encore's pages answer fresh.
    /// </summary>
    public sealed class NoCacheAttribute : ActionFilterAttribute
    {
        public override void OnResultExecuting(ResultExecutingContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-cache, no-store, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
            headers.Remove("Last-Modified");

            base.OnResultExecuting(context);
        }
    }
}
