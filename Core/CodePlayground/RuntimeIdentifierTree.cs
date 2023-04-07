using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace CodePlayground
{
    internal static class RuntimeIdentifierTree
    {
        private struct RuntimeIdentifierDescription
        {
            [JsonProperty(PropertyName = "#import")]
            public string[] Inherits { get; set; }
        }

        private struct RuntimeIdentifierInfo
        {
            [JsonProperty(PropertyName = "runtimes")]
            public Dictionary<string, RuntimeIdentifierDescription>? Runtimes { get; set; }
        }

        private static Dictionary<string, IReadOnlySet<string>> sDerivedRuntimes;
        static RuntimeIdentifierTree()
        {
            var frame = new StackFrame(0);
            var assemblyDir = frame.GetMethod()?.DeclaringType?.Assembly?.GetAssemblyDirectory();

            if (string.IsNullOrEmpty(assemblyDir))
            {
                throw new FileNotFoundException("Failed to find the directory of the current assembly!");
            }

            var runtimesFile = Path.Combine(assemblyDir, "runtime.json");
            string json = File.ReadAllText(runtimesFile);
            var deserialized = JsonConvert.DeserializeObject<RuntimeIdentifierInfo>(json);

            if (deserialized.Runtimes is null)
            {
                throw new IOException("Failed to deserialize JSON!");
            }

            ComputeDerivedRuntimes(deserialized.Runtimes);
        }

        [MemberNotNull(nameof(sDerivedRuntimes))]
        private static void ComputeDerivedRuntimes(IReadOnlyDictionary<string, RuntimeIdentifierDescription> identifiers)
        {
            var immediatelyDerived = new Dictionary<string, List<string>>();
            foreach (var identifier in identifiers.Keys)
            {
                var inherits = identifiers[identifier].Inherits;
                foreach (var inheritedIdentifier in inherits)
                {
                    if (!immediatelyDerived.ContainsKey(inheritedIdentifier))
                    {
                        immediatelyDerived.Add(inheritedIdentifier, new List<string>());
                    }

                    immediatelyDerived[inheritedIdentifier].Add(identifier);
                }

                if (!immediatelyDerived.ContainsKey(identifier))
                {
                    immediatelyDerived.Add(identifier, new List<string>());
                }
            }

            sDerivedRuntimes = new Dictionary<string, IReadOnlySet<string>>();
            var identifierStack = new Stack<string>();

            foreach (var identifier in identifiers.Keys)
            {
                PopulateDerivedIdentifier(identifier, immediatelyDerived, identifierStack);
                if (sDerivedRuntimes.Count >= immediatelyDerived.Count)
                {
                    break;
                }
            }
        }

        private static void PopulateDerivedIdentifier(string baseIdentifier, IReadOnlyDictionary<string, List<string>> immediatelyDerived, Stack<string> identifierStack)
        {
            if (sDerivedRuntimes.ContainsKey(baseIdentifier))
            {
                return;
            }

            if (identifierStack.Contains(baseIdentifier))
            {
                throw new ArgumentException("Inheritance recursion detected!");
            }

            identifierStack.Push(baseIdentifier);

            var immediateDerivations = immediatelyDerived[baseIdentifier];
            var result = new HashSet<string>();

            foreach (var identifier in immediateDerivations)
            {
                if (result.Contains(identifier))
                {
                    continue;
                }

                PopulateDerivedIdentifier(identifier, immediatelyDerived, identifierStack);
                var derivedRuntimes = sDerivedRuntimes[identifier].Append(identifier);

                foreach (var derivedIdentifier in derivedRuntimes)
                {
                    result.Add(derivedIdentifier);
                }
            }

            sDerivedRuntimes.Add(baseIdentifier, result);
            identifierStack.Pop();
        }

        public static bool Inherits(string derivedIdentifier, string baseIdentifier)
        {
            string derivedLower = derivedIdentifier.ToLower();
            string baseLower = baseIdentifier.ToLower();

            if (derivedLower == baseLower)
            {
                return true;
            }

            if (!sDerivedRuntimes.ContainsKey(baseLower))
            {
                return false;
            }

            return sDerivedRuntimes[baseLower].Contains(derivedLower);
        }
    }
}
