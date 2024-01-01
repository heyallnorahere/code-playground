using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Varena;

namespace CodePlayground
{
    public abstract class Application : IDisposable
    {
        private static Application? sInstance;
        private static readonly VirtualArenaManager sManager;

        static Application()
        {
            sInstance = null;
            sManager = new VirtualArenaManager();
        }

        public static Application Instance => sInstance ?? throw new InvalidOperationException();
        public static VirtualArenaManager ArenaManager => sManager;

        internal static int Main(string[] args)
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
            int exitCode = RunApplication(applicationType, args[1..]);

            return exitCode;
        }

        public static int RunApplication<T>(string[] args) where T : Application, new()
        {
            return RunApplication(typeof(T), args);
        }

        public static int RunApplication(Type applicationType, string[] args)
        {
            if (sInstance is not null)
            {
                throw new InvalidOperationException("An application is already running!");
            }

            if (!applicationType.Extends(typeof(Application)))
            {
                throw new InvalidCastException("The specified type does not extend \"Application!\"");
            }

            var constructor = applicationType.GetConstructor(Type.EmptyTypes);
            if (constructor is null)
            {
                throw new InvalidOperationException("The specified type does not have a constructor with no parameters!");
            }

            int exitCode = 0;
            using (var instance = (Application)constructor.Invoke(null))
            {
                instance.ApplyAttributes();
                sInstance = instance;

                Console.WriteLine($"Running application {instance.Title} version {instance.Version}");
                exitCode = instance.Run(args);
                
                Profiler.Shutdown();
            }

            sInstance = null;
            return exitCode;
        }

        public Application()
        {
            var assembly = GetType().Assembly;
            var versionAttribute = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();

            Title = GetType().Assembly.GetName().FullName;
            Version = Version.Parse(versionAttribute?.Version ?? "1.0.0.0");

            mDisposed = false;
        }

        ~Application()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            // nothing
        }

<<<<<<< HEAD
        protected void InitializeOptick(bool debug = false)
        {
            StateCallback callback = OnStateChanged;
            OptickImports.SetStateChangedCallback(callback);
            OptickMacros.IsDebug = debug;

            mOptickCallbackHandle?.Free();
            mOptickCallbackHandle = GCHandle.Alloc(callback);

            mOptickApp ??= new OptickApp(Title);
        }

        private bool OnStateChanged(State state)
        {
            OptickStateChanged?.Invoke(state);
            return true;
        }

        protected void ShutdownOptick()
        {
            mOptickApp?.Dispose();
            mOptickCallbackHandle?.Free();

            if (mOptickApp is not null)
            {
                OptickMacros.Shutdown();
            }

            mOptickApp = null;
            mOptickCallbackHandle = null;
        }

        public event Action<State>? OptickStateChanged;
=======
>>>>>>> 8ad2601 (replace optick with tracy (checkpoint))
        public string Title { get; internal set; }
        public Version Version { get; internal set; }
        public abstract bool IsRunning { get; }

        protected abstract int Run(string[] args);
        public abstract bool Quit(int exitCode);

        private void ApplyAttributes()
        {
            var type = GetType();
            var attributes = type.GetCustomAttributes();

            var descriptionAttributes = new List<ApplicationDescriptionAttribute>();
            foreach (var attribute in attributes)
            {
                if (attribute is ApplicationDescriptionAttribute descriptionAttribute)
                {
                    descriptionAttributes.Add(descriptionAttribute);
                }
            }

            descriptionAttributes.ForEach(attr => attr.Apply(this));
        }

        private bool mDisposed;
    }
}
