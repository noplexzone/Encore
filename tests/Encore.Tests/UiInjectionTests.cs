using System.Text;
using Encore.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Encore.Tests
{
    public class UiInjectionTests
    {
        private static HttpRequest Request(string method, string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            return context.Request;
        }

        [Theory]
        [InlineData("/", "", true)]
        [InlineData("/artist/39ab1aed-75e0-4140-bd47-540276886b60", "", true)]
        [InlineData("/settings/metadata", "", true)]
        [InlineData("/lidarr/artist/39ab1aed-75e0-4140-bd47-540276886b60", "/lidarr", true)]
        [InlineData("/api/v1/artist", "", false)]
        [InlineData("/lidarr/api/v1/artist", "/lidarr", false)]
        [InlineData("/signalr/messages", "", false)]
        [InlineData("/MediaCover/1/poster.jpg", "", false)]
        [InlineData("/Content/styles.css", "", false)]
        [InlineData("/artwork/3", "", false)]
        [InlineData("/videos", "", false)]
        [InlineData("/encore/ui.js", "", false)]
        [InlineData("/login", "", false)]
        [InlineData("/initialize.json", "", false)]
        [InlineData("/apidocs", "", true)]
        public void Only_ui_pages_are_injected(string path, string urlBase, bool expected)
        {
            Assert.Equal(expected, UiInjectionMiddleware.IsPageRequest(Request("GET", path), urlBase));
        }

        [Fact]
        public void Posts_are_never_injected()
        {
            Assert.False(UiInjectionMiddleware.IsPageRequest(Request("POST", "/"), ""));
        }

        [Fact]
        public void Script_goes_before_the_closing_body()
        {
            var html = Encoding.UTF8.GetBytes("<html><body><div id=\"root\"></div></body></html>");
            var result = Encoding.UTF8.GetString(UiInjectionMiddleware.Inject(html, "/lidarr"));

            Assert.Equal("<html><body><div id=\"root\"></div><script src=\"/lidarr/encore/ui.js\" defer></script></body></html>", result);
        }

        [Fact]
        public void Script_is_added_once()
        {
            var html = Encoding.UTF8.GetBytes("<body></body>");
            var once = UiInjectionMiddleware.Inject(html, "");

            Assert.Equal(once, UiInjectionMiddleware.Inject(once, ""));
        }
    }
}
