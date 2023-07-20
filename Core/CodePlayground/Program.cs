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

            string path = args[0];
            var assembly = Assembly.LoadFrom(path);

            var loadedAppAttribute = assembly.GetCustomAttribute<LoadedApplicationAttribute>();
            if (loadedAppAttribute is null)
            {
                throw new InvalidOperationException("Attempted to load a non-application DLL!");
            }

            var applicationType = loadedAppAttribute.ApplicationType;
            int exitCode = Application.RunApplication(applicationType, args[1..]);

            return exitCode;
        }
    }
}