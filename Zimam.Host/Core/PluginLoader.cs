using System.Reflection;
using Zimam.Abstractions;

namespace Zimam.Host.Core;

public static class PluginLoader
{
    public static Dictionary<string, Type> DiscoverDrivers(string pluginsPath)
    {
        var driverTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pluginsPath))
        {
            Directory.CreateDirectory(pluginsPath);
            return driverTypes;
        }

        foreach (var dll in Directory.GetFiles(pluginsPath, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                var types = assembly.GetTypes()
                    .Where(t => typeof(IDeviceDriver).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                foreach (var type in types)
                {
                    if (Activator.CreateInstance(type) is IDeviceDriver temp)
                        driverTypes[temp.DriverType] = type;
                }
            }
            catch (Exception ex) { Console.WriteLine($"Plugin error {dll}: {ex.Message}"); }
        }
        return driverTypes;
    }
}
