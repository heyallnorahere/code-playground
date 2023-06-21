using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CodePlayground
{
    public static class Utilities
    {
        public static bool IsNullable(this Type type)
        {
            if (type.IsValueType)
            {
                return Nullable.GetUnderlyingType(type) != null;
            }

            return true;
        }

        public static T CreateDynamicInstance<T>(params object?[] args) => (T)CreateDynamicInstance(typeof(T), args);
        public static object CreateDynamicInstance(Type type, params object?[] args)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var constructor in constructors)
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length < args.Length)
                {
                    continue;
                }

                var parameterData = new object?[parameters.Length];
                Array.Copy(args, parameterData, args.Length);

                bool constructorValid = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var parameterInfo = parameters[i];
                    if (i >= args.Length)
                    {
                        var defaultValue = parameterInfo.DefaultValue;
                        if (DBNull.Value.Equals(defaultValue))
                        {
                            constructorValid = false;
                            break;
                        }
                        else
                        {
                            parameterData[i] = defaultValue;
                            continue;
                        }
                    }

                    var parameterType = parameterInfo.ParameterType;
                    var dataType = args[i]?.GetType();

                    if (!(dataType?.IsAssignableTo(parameterType) ?? parameterType.IsNullable()))
                    {
                        try
                        {
                            parameterData[i] = Convert.ChangeType(args[i], parameterType);
                        }
                        catch (Exception)
                        {
                            constructorValid = false;
                            break;
                        }
                    }
                    else
                    {
                        parameterData[i] = args[i];
                    }
                }

                if (!constructorValid)
                {
                    continue;
                }

                return constructor.Invoke(parameterData);
            }

            throw new ArgumentException("Failed to find a suitable constructor!");
        }

        public static IReadOnlySet<T> SplitFlags<T>(this T flags) where T : struct, Enum
        {
            var type = typeof(T);
            if (type.GetCustomAttribute<FlagsAttribute>() is null)
            {
                throw new ArgumentException("Enum type is not a flag!");
            }

            var enumValues = Enum.GetValues<T>();
            var flagValues = new HashSet<T>();

            foreach (var value in enumValues)
            {
                if (flags.HasFlag(value))
                {
                    flagValues.Add(value);
                }
            }

            return flagValues;
        }

        public static bool Extends<T>(this Type derived) where T : class
        {
            return derived.Extends(typeof(T));
        }

        public static bool Extends(this Type derived, Type baseType)
        {
            if (derived == baseType)
            {
                return true;
            }

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

        public static List<ILInstruction>? GetILAsInstructionList(this MethodBody body, Module module)
        {
            var code = body.GetILAsByteArray();
            if (code is null)
            {
                return null;
            }

            var parser = new ILParser();
            parser.Parse(code, module);

            return parser.Instructions;
        }

        public static string? GetAssemblyDirectory(this Assembly assembly) => Path.GetDirectoryName(assembly.Location);
    }
}
