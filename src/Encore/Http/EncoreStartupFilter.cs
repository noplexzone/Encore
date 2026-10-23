using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Http
{
    /// <summary>
    /// Lidarr only registers controllers from its own assemblies. Lidarr's container does pick up
    /// every interface a plugin class implements, including IStartupFilter, so this runs while
    /// the web host builds its pipeline and adds the plugin's controllers before routes are built.
    /// </summary>
    public class EncoreStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                var partManager = app.ApplicationServices.GetService<ApplicationPartManager>();
                var assembly = typeof(EncoreStartupFilter).Assembly;

                if (partManager != null && !partManager.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == assembly))
                {
                    partManager.ApplicationParts.Add(new AssemblyPart(assembly));
                }

                next(app);
            };
        }
    }
}
