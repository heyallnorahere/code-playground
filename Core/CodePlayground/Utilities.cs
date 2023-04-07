using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CodePlayground
{
    public static class Utilities
    {
        public static bool Extends(this Type derived, Type baseType)
        {
            if (derived.BaseType != baseType)
            {
                return derived.BaseType?.Extends(baseType) ?? false;
            }
            else
            {
                return true;
            }
        }

        private static IReadOnlyDictionary<string, MethodInfo> FindHandlerMethods(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var handlers = new Dictionary<string, MethodInfo>();

            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<EventHandlerAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                string name = attribute.EventName ?? method.Name;
                if (handlers.ContainsKey(name))
                {
                    throw new ArgumentException("Duplicate event handler!");
                }

                handlers.Add(name, method);
            }

            return handlers;
        }

        public static EventInfo? GetEventRecursive(this Type type, string eventName, BindingFlags bindingFlags)
        {
            var eventInfo = type.GetEvent(eventName, bindingFlags);
            if (eventInfo is null)
            {
                eventInfo = type.BaseType?.GetEventRecursive(eventName, bindingFlags);
            }

            return eventInfo;
        }

        public static int BindHandlers<T>(this object eventObject, T handlerObject)
        {
            var eventType = eventObject.GetType();
            var handlerType = typeof(T);

            if (eventType.IsValueType || handlerType.IsValueType)
            {
                throw new ArgumentException("Both types must be pass-by-reference!");
            }

            int boundCount = 0;
            var handlerMethods = FindHandlerMethods(handlerType);
            foreach (var eventName in handlerMethods.Keys)
            {
                var eventInfo = eventType.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public);
                if (eventInfo is null)
                {
                    continue;
                }

                var delegateType = eventInfo.EventHandlerType;
                if (delegateType is null)
                {
                    continue;
                }

                var method = handlerMethods[eventName];
                var handler = Delegate.CreateDelegate(delegateType, handlerObject, method);

                eventInfo.AddEventHandler(eventObject, handler);
                boundCount++;
            }

            return boundCount;
        }

        public static int UnbindHandlers<T>(this object eventObject, T handlerObject)
        {
            var eventType = eventObject.GetType();
            var handlerType = typeof(T);

            if (!eventType.IsByRef || !handlerType.IsByRef)
            {
                throw new ArgumentException("Both types must be pass-by-reference!");
            }

            int unboundCount = 0;
            var handlerMethods = FindHandlerMethods(handlerType);
            foreach (var eventName in handlerMethods.Keys)
            {
                var eventInfo = eventType.GetEventRecursive(eventName, BindingFlags.Instance | BindingFlags.Public);
                if (eventInfo is null)
                {
                    continue;
                }

                var delegateType = eventInfo.EventHandlerType;
                if (delegateType is null)
                {
                    continue;
                }

                var method = handlerMethods[eventName];
                var handler = Delegate.CreateDelegate(delegateType, handlerObject, method);

                eventInfo.RemoveEventHandler(eventObject, handler);
                unboundCount++;
            }

            return unboundCount;
        }

        public static string? GetAssemblyDirectory(this Assembly assembly) => Path.GetDirectoryName(assembly.Location);
    }
}
