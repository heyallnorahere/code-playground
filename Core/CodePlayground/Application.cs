using Optick.NET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodePlayground
{
    public abstract class Application : IDisposable
    {
        static Application()
        {
            sInstance = null;
        }

        private static Application? sInstance;
        public static Application Instance => sInstance ?? throw new InvalidOperationException();

        internal static int RunApplication<T>(string[] args) where T : Application, new()
        {
            return RunApplication(typeof(T), args);
        }

        internal static int RunApplication(Type applicationType, string[] args)
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

                var copiedBinaries = instance.CopiedBinaries;
                if (copiedBinaries.Length > 0)
                {
                    var assemblyDir = applicationType.Assembly.GetAssemblyDirectory();
                    if (string.IsNullOrEmpty(assemblyDir))
                    {
                        throw new ArgumentException("Failed to retrieve the directory of the application assembly!");
                    }

                    CopyBinaries(assemblyDir, copiedBinaries);
                }

                Console.WriteLine($"Running application {instance.Title} version {instance.Version}");
                exitCode = instance.Run(args);
                
                instance.ShutdownOptick();
            }

            sInstance = null;
            return exitCode;
        }

        private static void CopyBinaries(string assemblyDirectory, string[] copiedBinaries)
        {
            string runtimeDirectory = Path.Join(assemblyDirectory, "runtimes");
            if (!Directory.Exists(runtimeDirectory))
            {
                return;
            }

            string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
            string[] subdirectories = Directory.GetDirectories(runtimeDirectory);
            foreach (string subdirectory in subdirectories)
            {
                string binaryDirectory = Path.Join(subdirectory, "native");
                if (!Directory.Exists(binaryDirectory))
                {
                    continue;
                }

                var subdirRuntimeIdentifier = Path.GetFileName(subdirectory.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(subdirRuntimeIdentifier))
                {
                    continue;
                }

                if (RuntimeIdentifierTree.Inherits(runtimeIdentifier, subdirRuntimeIdentifier))
                {
                    foreach (string filename in copiedBinaries)
                    {
                        string sourcePath = Path.Join(binaryDirectory, filename);
                        string destinationPath = Path.Join(assemblyDirectory, filename);

                        if (File.Exists(sourcePath))
                        {
                            File.Copy(sourcePath, destinationPath, true);
                        }
                    }
                }
            }
        }

        public Application()
        {
            var assembly = GetType().Assembly;
            var versionAttribute = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();

            Title = $"Untitled Application ({GetType().Name})";
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

        [Conditional("DEBUG")]
        protected void InitializeOptick()
        {
            StateCallback callback = OnStateChanged;
            OptickImports.SetStateChangedCallback(callback);

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
                OptickImports.Shutdown();
            }

            mOptickApp = null;
            mOptickCallbackHandle = null;
        }

        public event Action<State>? OptickStateChanged;
        public string Title { get; internal set; }
        public Version Version { get; internal set; }
        public abstract bool IsRunning { get; }
        protected virtual string[] CopiedBinaries => Array.Empty<string>();

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
        private OptickApp? mOptickApp;
        private GCHandle? mOptickCallbackHandle;
    }
}
