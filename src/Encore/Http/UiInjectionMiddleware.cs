using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbDrone.Core.Configuration;

namespace Encore.Http
{
    /// <summary>
    /// Plugins can't add to Lidarr's own pages, so this adds a small script to the page Lidarr
    /// serves for its UI. The script puts Encore's buttons on the artist page (see ui.js).
    /// Only page requests are touched; API calls, images and other files pass straight through.
    /// </summary>
    public class UiInjectionMiddleware
    {
        private static readonly string[] SkippedPrefixes = ["/api", "/signalr", "/feed", "/mediacover", "/artwork", "/videos", "/encore", "/login", "/logout", "/content", "/initialize"];

        private readonly RequestDelegate _next;

        public UiInjectionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var urlBase = context.RequestServices.GetService<IConfigFileProvider>()?.UrlBase ?? string.Empty;

            if (!IsPageRequest(context.Request, urlBase))
            {
                await _next(context);
                return;
            }

            // Lidarr compresses responses, which would hide the page from us
            context.Request.Headers.Remove("Accept-Encoding");

            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = original;
            }

            var body = buffer.ToArray();

            if (context.Response.StatusCode == StatusCodes.Status200OK &&
                context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                !context.Response.Headers.ContainsKey("Content-Encoding"))
            {
                body = Inject(body, urlBase);
                context.Response.ContentLength = body.Length;
            }

            await original.WriteAsync(body);
        }

        public static bool IsPageRequest(HttpRequest request, string urlBase)
        {
            if (!HttpMethods.IsGet(request.Method))
            {
                return false;
            }

            var path = request.Path.Value ?? "/";

            if (urlBase.Length > 0 && path.StartsWith(urlBase, StringComparison.OrdinalIgnoreCase))
            {
                path = path[urlBase.Length..];
            }

            // Files have an extension; the UI's own routes (/artist/..., /settings/...) don't
            if (Path.HasExtension(path))
            {
                return false;
            }

            return !SkippedPrefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                             path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly string Version = typeof(UiInjectionMiddleware).Assembly.GetName().Version?.ToString() ?? "0";

        public static byte[] Inject(byte[] html, string urlBase)
        {
            var text = Encoding.UTF8.GetString(html);
            // Versioned, so a browser that kept an older copy fetches the new one after an update
            var tag = $"<script src=\"{urlBase}/encore/ui.js?v={Version}\" defer></script>";

            if (text.Contains("/encore/ui.js", StringComparison.Ordinal))
            {
                return html;
            }

            var index = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            text = index >= 0 ? text.Insert(index, tag) : text + tag;

            return Encoding.UTF8.GetBytes(text);
        }
    }
}
