using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CodePlayground.Graphics.Shaders.Transpilers
{
    internal struct GLSLMethodInfo
    {
        public string Source { get; set; }
        public IReadOnlyList<MethodInfo> Dependencies { get; set; }
        public bool IsBuiltin { get; set; }
    }

    internal struct EvaluationMethodInfo
    {
        public List<MethodInfo> Dependencies { get; set; }
        public MethodInfo Method { get; set; }
        public List<MethodInfo> Dependents { get; set; }
    }

    internal sealed class GLSLTranspiler : ShaderTranspiler
    {
        private static readonly Dictionary<Type, string> sPrimitiveTypeNames;
        static GLSLTranspiler()
        {
            sPrimitiveTypeNames = new Dictionary<Type, string>
            {
                [typeof(int)] = "int",
                [typeof(uint)] = "uint",
                [typeof(bool)] = "bool",
                [typeof(float)] = "float",
                [typeof(double)] = "double",
                [typeof(void)] = "void"
            };
        }

        public GLSLTranspiler()
        {
            mDefinedTypeNames = new Dictionary<Type, string>();
            mFunctionNames = new Dictionary<MethodInfo, string>();
            mDependencyGraph = new Dictionary<MethodInfo, GLSLMethodInfo>();
        }

        private string GetFunctionName(MethodInfo method, Type shaderType)
        {
            if (mFunctionNames.ContainsKey(method))
            {
                return mFunctionNames[method];
            }

            var attribute = method.GetCustomAttribute<BuiltinShaderFunctionAttribute>();
            if (attribute is not null)
            {
                mFunctionNames.Add(method, attribute.Name);
                return attribute.Name;
            }

            var nameList = new List<string>();

            var declaringType = method.DeclaringType;
            if (declaringType is not null && declaringType != shaderType)
            {
                string declaringTypeName = GetTypeName(declaringType, shaderType, false);
                nameList.Add(declaringTypeName);
            }

            nameList.Add(method.Name);
            if (method.IsConstructedGenericMethod)
            {
                var genericArguments = method.GetGenericArguments();
                foreach (var argument in genericArguments)
                {
                    string argumentName = GetTypeName(argument, shaderType, true);
                    nameList.Add(argumentName);
                }
            }

            string glslName = string.Empty;
            for (int i = 0; i < nameList.Count; i++)
            {
                if (i > 0)
                {
                    glslName += ", ";
                }

                glslName += nameList[i];
            }

            mFunctionNames.Add(method, glslName);
            return glslName;
        }

        private string GetTypeName(Type type, Type shaderType, bool asType)
        {
            if (sPrimitiveTypeNames.ContainsKey(type))
            {
                return sPrimitiveTypeNames[type];
            }

            if (mDefinedTypeNames.ContainsKey(type))
            {
                return mDefinedTypeNames[type];
            }

            var attribute = type.GetCustomAttribute<PrimitiveShaderTypeAttribute>();
            if (attribute is not null)
            {
                string primitiveName = attribute.Name;
                if (type.IsConstructedGenericType)
                {
                    var genericArguments = type.GetGenericArguments();
                    if (genericArguments.Length != 1)
                    {
                        throw new InvalidOperationException("Cannot have more than 1 generic argument!");
                    }

                    var argument = genericArguments[0];
                    if (!sPrimitiveTypeNames.ContainsKey(argument))
                    {
                        throw new InvalidOperationException("Must use a GLSL primitive as a generic argument!");
                    }

                    if (argument == typeof(void))
                    {
                        throw new InvalidOperationException("Cannot use void as a value!");
                    }

                    primitiveName.Insert(0, sPrimitiveTypeNames[argument][0..1]);
                }

                mDefinedTypeNames.Add(type, primitiveName);
                return primitiveName;
            }

            if (type.IsClass && asType)
            {
                throw new InvalidOperationException("Cannot use a class as a shader type!");
            }

            var declaredTypeNames = new List<string>();
            Type? currentType = type;
            Type? lastType = null;

            bool declaredByShader = false;
            while (currentType is not null)
            {
                if (currentType == shaderType)
                {
                    declaredByShader = true;
                    break;
                }

                declaredTypeNames.Insert(0, currentType.Name);
                if (currentType.IsConstructedGenericType)
                {
                    var genericArguments = currentType.GetGenericArguments();
                    for (int i = 0; i < genericArguments.Length; i++)
                    {
                        var argumentType = genericArguments[i];
                        var argumentTypeName = GetTypeName(argumentType, shaderType, true);
                        declaredTypeNames.Insert(i + 1, argumentTypeName);
                    }
                }

                lastType = currentType;
                currentType = currentType.DeclaringType;
            }

            var nameList = new List<string>();
            if (!declaredByShader)
            {
                var namespaceName = lastType!.Namespace;
                if (namespaceName is not null)
                {
                    var segments = namespaceName.Split('.');
                    nameList.AddRange(segments);
                }
            }

            nameList.AddRange(declaredTypeNames);
            string glslName = string.Empty;
            for (int i = 0; i < nameList.Count; i++)
            {
                if (i > 0)
                {
                    glslName += '_';
                }

                glslName += nameList[i];
            }

            mDefinedTypeNames.Add(type, glslName);
            return glslName;
        }

        private GLSLMethodInfo TranspileMethod(Type type, MethodInfo method, bool entrypoint)
        {
            var body = method.GetMethodBody();
            var instructions = body?.GetILAsInstructionList(method.Module);

            if (body is null || instructions is null)
            {
                throw new ArgumentException("Cannot transpile a method without a body!");
            }

            var builder = new StringBuilder();
            string returnTypeString, parameterString;

            if (entrypoint)
            {
                returnTypeString = "void";
                parameterString = string.Empty;
            }
            else
            {
                returnTypeString = GetTypeName(method.ReturnType, type, true);
                parameterString = string.Empty;

                var parameters = method.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        parameterString += ", ";
                    }

                    var parameter = parameters[i];
                    parameterString += $"{GetTypeName(parameter.ParameterType, type, true)} {parameter.Name}";
                }
            }

            builder.AppendLine($"{returnTypeString} {method.Name}({parameterString}) {{");

            var evaluationStack = new Stack<object?>();
            var dependencies = new List<MethodInfo>();
            foreach (var instruction in instructions)
            {
                // todo: interpret instruction

                if (instruction.Operand is MethodInfo dependency)
                {
                    dependencies.Add(dependency);
                }
            }

            builder.AppendLine("}");
            return new GLSLMethodInfo
            {
                Source = builder.ToString(),
                Dependencies = dependencies,
                IsBuiltin = false
            };
        }

        private void ClearData()
        {
            mDefinedTypeNames.Clear();
            mFunctionNames.Clear();
            mDependencyGraph.Clear();
        }

        private void ProcessMethod(Type type, MethodInfo method, bool entrypoint)
        {
            if (mDependencyGraph.ContainsKey(method))
            {
                return;
            }

            var attribute = method.GetCustomAttribute<BuiltinShaderFunctionAttribute>();
            if (attribute is not null)
            {
                mDependencyGraph.Add(method, new GLSLMethodInfo
                {
                    Source = string.Empty,
                    Dependencies = Array.Empty<MethodInfo>(),
                    IsBuiltin = true
                });

                return;
            }

            var info = TranspileMethod(type, method, entrypoint);
            mDependencyGraph.Add(method, info);

            foreach (var dependency in info.Dependencies)
            {
                ProcessMethod(type, dependency, false);
            }
        }

        protected override string TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage)
        {
            // todo: process inputs/outputs

            var builder = new StringBuilder();
            builder.AppendLine("#version 450");

            // todo: define inputs/outputs
            // todo: process dependency tree

            ProcessMethod(type, entrypoint, true);

            var dependencyInfo = new List<EvaluationMethodInfo>();
            var dependencyInfoIndices = new Dictionary<MethodInfo, int>();

            foreach (var method in mDependencyGraph.Keys)
            {
                var dependencies = mDependencyGraph[method].Dependencies;
                var definedDependencies = dependencies.Where(dependency => dependency.GetCustomAttribute<BuiltinShaderFunctionAttribute>() is null);

                foreach (var dependency in definedDependencies)
                {
                    if (dependencyInfoIndices.ContainsKey(dependency))
                    {
                        int index = dependencyInfoIndices[dependency];
                        var info = dependencyInfo[index];
                        info.Dependents.Add(method);
                    }
                    else
                    {
                        dependencyInfoIndices.Add(dependency, dependencyInfo.Count);
                        dependencyInfo.Add(new EvaluationMethodInfo
                        {
                            Dependencies = new List<MethodInfo>(),
                            Method = dependency,
                            Dependents = new List<MethodInfo>
                            {
                                method
                            }
                        });
                    }
                }

                if (dependencyInfoIndices.ContainsKey(method))
                {
                    int index = dependencyInfoIndices[method];
                    var info = dependencyInfo[index];
                    info.Dependencies.AddRange(definedDependencies);
                }
                else
                {
                    dependencyInfoIndices.Add(method, dependencyInfo.Count);
                    dependencyInfo.Add(new EvaluationMethodInfo
                    {
                        Dependencies = definedDependencies.ToList(),
                        Method = method,
                        Dependents = new List<MethodInfo>()
                    });
                }
            }

            dependencyInfo.Sort((a, b) => a.Dependencies.Count.CompareTo(b.Dependencies.Count));
            for (int i = 0; i < dependencyInfo.Count; i++)
            {
                dependencyInfoIndices[dependencyInfo[i].Method] = i;
            }

            IEnumerable<MethodInfo> evaluationList = new MethodInfo[]
            {
                dependencyInfo[0].Method
            };

            var insertedFunctions = new HashSet<MethodInfo>();
            while (evaluationList.Count() > 0)
            {
                var newEvaluationList = new HashSet<MethodInfo>();
                foreach (var method in evaluationList)
                {
                    var info = dependencyInfo[dependencyInfoIndices[method]];

                    bool hasDependencies = true;
                    foreach (var dependency in info.Dependencies)
                    {
                        if (!insertedFunctions.Contains(dependency))
                        {
                            hasDependencies = false;
                            newEvaluationList.Add(dependency);
                        }
                    }

                    if (!hasDependencies)
                    {
                        break;
                    }

                    builder.AppendLine(mDependencyGraph[method].Source);
                    insertedFunctions.Add(method);

                    foreach (var dependent in info.Dependents)
                    {
                        newEvaluationList.Add(dependent);
                    }
                }

                evaluationList = newEvaluationList;
            }

            ClearData();
            return builder.ToString();
        }

        private readonly Dictionary<Type, string> mDefinedTypeNames;
        private readonly Dictionary<MethodInfo, string> mFunctionNames;
        private readonly Dictionary<MethodInfo, GLSLMethodInfo> mDependencyGraph;
    }
}