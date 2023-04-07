using System;

namespace CodePlayground
{
    public abstract class ApplicationDescriptionAttribute : Attribute
    {
        public abstract void Apply(Application application);
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ApplicationTitleAttribute : ApplicationDescriptionAttribute
    {
        public ApplicationTitleAttribute(string title)
        {
            mTitle = title;
        }

        public override void Apply(Application application)
        {
            application.Title = mTitle;
        }

        private readonly string mTitle;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ApplicationVersionAttribute : ApplicationDescriptionAttribute
    {
        public ApplicationVersionAttribute(string version)
        {
            mVersion = Version.Parse(version);
        }

        public override void Apply(Application application)
        {
            application.Version = mVersion;
        }

        private readonly Version mVersion;
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class LoadedApplicationAttribute : Attribute
    {
        public LoadedApplicationAttribute(Type type)
        {
            ApplicationType = type;
        }

        internal Type ApplicationType { get; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class EventHandlerAttribute : Attribute
    {
        public EventHandlerAttribute()
        {
            EventName = null;
        }

        public EventHandlerAttribute(string eventName)
        {
            EventName = eventName;
        }

        public string? EventName { get; }
    }
}
