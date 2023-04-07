using System;
using System.Reflection;

namespace CodePlayground
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length <= 0)
            {
                throw new ArgumentException("No assembly was provided!");
            }

            var assembly = Assembly.LoadFrom(args[0]);
            var loadedAppAttribute = assembly.GetCustomAttribute<LoadedApplicationAttribute>();
            if (loadedAppAttribute is null)
            {
                throw new InvalidOperationException("Attempted to load a non-application DLL!");
            }

            var applicationType = loadedAppAttribute.ApplicationType;
            return Application.RunApplication(applicationType, args[1..]);
        }
    }
}