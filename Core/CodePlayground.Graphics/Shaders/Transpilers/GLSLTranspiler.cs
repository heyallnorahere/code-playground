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

    internal struct StageIOField
    {
        public string Direction { get; set; }
        public int Location { get; set; }
        public string TypeName { get; set; }
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
            mFieldNames = new Dictionary<FieldInfo, string>();
            mStageIO = new Dictionary<string, StageIOField>();
            mDependencyGraph = new Dictionary<MethodInfo, GLSLMethodInfo>();
        }

        private string GetFieldName(FieldInfo field, Type shaderType)
        {
            if (mFieldNames.ContainsKey(field))
            {
                return mFieldNames[field];
            }

            var attribute = field.GetCustomAttribute<ShaderFieldNameAttribute>();
            string name = attribute?.Name ?? field.Name;

            var declaringType = field.DeclaringType;
            if (field.IsStatic && declaringType is not null && field.DeclaringType != shaderType)
            {
                string typeName = GetTypeName(declaringType, shaderType, false);
                name = $"{typeName}_{name}";
            }

            mFieldNames.Add(field, name);
            return name;
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

            var attribute = type.GetCustomAttribute<PrimitiveShaderTypeAttribute>();
            if (asType && type.IsClass && attribute is null)
            {
                throw new InvalidOperationException("Cannot use a class as a shader type!");
            }

            if (mDefinedTypeNames.ContainsKey(type))
            {
                return mDefinedTypeNames[type];
            }

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

        private static string CreateParameterExpressionString(Stack<string> evaluationStack, int parameterCount)
        {
            string expressionString = string.Empty;
            for (int i = parameterCount - 1; i >= 0; i--)
            {
                string parameterSeparator = i > 0 ? ", " : string.Empty;
                var currentParameter = parameterSeparator + evaluationStack.Pop();
                expressionString = currentParameter + expressionString;
            }

            return expressionString;
        }

        private GLSLMethodInfo TranspileMethod(Type type, MethodInfo method, bool entrypoint)
        {
            var body = method.GetMethodBody();
            var instructions = body?.GetILAsInstructionList(method.Module);

            if (body is null || instructions is null)
            {
                throw new ArgumentException("Cannot transpile a method without a body!");
            }

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
                    var parameterName = parameter.Name ?? $"param_{i}";

                    parameterString += $"{GetTypeName(parameter.ParameterType, type, true)} {parameterName}";
                }
            }

            var functionName = GetFunctionName(method, type);
            var builder = new StringBuilder();
            builder.AppendLine($"{returnTypeString} {functionName}({parameterString}) {{");

            var localVariables = body.LocalVariables;
            for (int i = 0; i < localVariables.Count; i++)
            {
                string variableTypeName = GetTypeName(localVariables[i].LocalType, type, true);
                builder.AppendLine($"{variableTypeName} var_{i};");
            }

            var evaluationStack = new Stack<string>();
            var dependencies = new List<MethodInfo>();

            int skipTo = -1;
            foreach (var instruction in instructions)
            {
                if (skipTo > instruction.Offset)
                {
                    continue;
                }

                var opCode = instruction.OpCode;
                var name = opCode.Name?.ToLower();

                if (string.IsNullOrEmpty(name) || name == "nop")
                {
                    continue;
                }

                // https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes?view=net-7.0
                if (name.StartsWith("call"))
                {
                    MethodInfo invokedMethod;
                    if (instruction.Operand is MethodInfo operand)
                    {
                        invokedMethod = operand;
                        dependencies.Add(operand);
                    }
                    else
                    {
                        throw new InvalidOperationException("Cannot dynamically call shader functions!");
                    }

                    var parameters = invokedMethod.GetParameters();
                    var expressionString = CreateParameterExpressionString(evaluationStack, parameters.Length);

                    if (!invokedMethod.IsStatic)
                    {
                        evaluationStack.Pop();

                        var declaringType = invokedMethod.DeclaringType;
                        if (declaringType is null || !type.Extends(declaringType))
                        {
                            throw new InvalidOperationException("Cannot call an instance method outside of the shader class!");
                        }
                    }

                    string invokedFunctionName = GetFunctionName(invokedMethod, type);
                    string invocationExpression = $"{invokedFunctionName}({expressionString})";

                    if (invokedMethod.ReturnType != typeof(void))
                    {
                        evaluationStack.Push(invocationExpression);
                    }
                    else
                    {
                        builder.AppendLine($"{invocationExpression};");
                    }
                }
                else if (name.StartsWith("ld"))
                {
                    string loadType = name[2..];

                    string expression;
                    if (instruction.Operand is FieldInfo field)
                    {
                        var fieldName = GetFieldName(field, type);
                        if (loadType.StartsWith("fld"))
                        {
                            var parentExpression = evaluationStack.Pop();
                            expression = parentExpression != "this" ? $"{parentExpression}.{fieldName}" : fieldName;

                            if (entrypoint)
                            {
                                var layoutAttribute = field.GetCustomAttribute<LayoutAttribute>();
                                if (layoutAttribute is not null && layoutAttribute.Location >= 0)
                                {
                                    expression = "_input_" + expression.Replace(".", "_");
                                    if (!mStageIO.ContainsKey(expression))
                                    {
                                        mStageIO.Add(expression, new StageIOField
                                        {
                                            Direction = "in",
                                            Location = layoutAttribute.Location,
                                            TypeName = GetTypeName(field.FieldType, type, true)
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            expression = fieldName;
                        }
                    }
                    else if (loadType.StartsWith("loc"))
                    {
                        int variableIndex;
                        if (instruction.Operand is null)
                        {
                            variableIndex = int.Parse(name[(name.Length - 1)..]);
                        }
                        else
                        {
                            variableIndex = Convert.ToInt32(instruction.Operand);
                        }

                        expression = $"var_{variableIndex}";
                    }
                    else if (loadType.StartsWith("arg"))
                    {
                        int argumentIndex;
                        if (instruction.Operand is null)
                        {
                            argumentIndex = int.Parse(name[(name.Length - 1)..]);
                        }
                        else
                        {
                            argumentIndex = Convert.ToInt32(instruction.Operand);
                        }

                        if (!method.IsStatic)
                        {
                            argumentIndex--;
                        }

                        var parameters = method.GetParameters();
                        expression = argumentIndex < 0 ? "this" : parameters[argumentIndex].Name ?? $"param_{argumentIndex}";
                    }
                    else if (loadType.StartsWith("elem"))
                    {
                        var index = evaluationStack.Pop();
                        var array = evaluationStack.Pop();
                        expression = $"{array}[{index}]";
                    }
                    else if (instruction.Operand is string)
                    {
                        throw new InvalidOperationException("Strings are not permitted in shaders!");
                    }
                    else
                    {
                        expression = instruction.Operand?.ToString() ?? throw new InvalidOperationException("Null values are not permitted in shaders!");
                    }

                    evaluationStack.Push(expression);
                }
                else if (name.StartsWith("st"))
                {
                    var storeType = name[2..];

                    if (instruction.Operand is FieldInfo field)
                    {
                        var expression = evaluationStack.Pop();

                        var destination = GetFieldName(field, type);
                        if (storeType.StartsWith("fld"))
                        {
                            var destinationObject = evaluationStack.Pop();
                            destination = $"{destinationObject}.{destination}";
                        }

                        builder.AppendLine($"{destination} = {expression};");
                    }
                    else if (storeType.StartsWith("loc"))
                    {
                        int variableIndex;
                        if (instruction.Operand is null)
                        {
                            variableIndex = int.Parse(name[(name.Length - 1)..]);
                        }
                        else
                        {
                            variableIndex = Convert.ToInt32(instruction.Operand);
                        }

                        var expression = evaluationStack.Pop();
                        builder.AppendLine($"var_{variableIndex} = {expression};");
                    }
                    else if (storeType.StartsWith("elem"))
                    {
                        var expression = evaluationStack.Pop();
                        var index = evaluationStack.Pop();
                        var array = evaluationStack.Pop();

                        builder.AppendLine($"({array})[{index}] = {expression};");
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported store operation!");
                    }
                }
                else if (name.StartsWith("br"))
                {
                    skipTo = Convert.ToInt32(instruction.Operand!);
                }
                else if (name.StartsWith("add"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"({lhs} + {rhs})");
                }
                else if (name.StartsWith("sub"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"({lhs} - {rhs})");
                }
                else if (name.StartsWith("mul"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"({lhs} * {rhs})");
                }
                else if (name.StartsWith("div"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"({lhs} / {rhs})");
                }
                else
                {
                    // explicit cases
                    switch (name)
                    {
                        case "pop":
                            {
                                var expression = evaluationStack.Pop();
                                builder.AppendLine($"{expression};");
                            }
                            break;
                        case "initobj":
                            evaluationStack.Pop();
                            break;
                        case "newobj":
                            {
                                var constructor = (ConstructorInfo)instruction.Operand!;
                                string typeName = GetTypeName(constructor.DeclaringType!, type, true);

                                var parameters = constructor.GetParameters();
                                var expressionString = CreateParameterExpressionString(evaluationStack, parameters.Length);

                                evaluationStack.Push($"{typeName}({expressionString})");
                            }
                            break;
                        case "ret":
                            builder.AppendLine($"return {evaluationStack.Pop()};");
                            break;
                        default:
                            throw new InvalidOperationException($"Instruction {name} has not been implemented yet!");
                    }
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
            mFieldNames.Clear();
            mStageIO.Clear();
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
            var builder = new StringBuilder();
            builder.AppendLine("#version 450");

            ProcessMethod(type, entrypoint, true);

            foreach (string fieldName in mStageIO.Keys)
            {
                var fieldData = mStageIO[fieldName];
                builder.AppendLine($"layout(location = {fieldData.Location}) {fieldData.Direction} {fieldData.TypeName} {fieldName};");
            }

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
        private readonly Dictionary<FieldInfo, string> mFieldNames;
        private readonly Dictionary<string, StageIOField> mStageIO;
        private readonly Dictionary<MethodInfo, GLSLMethodInfo> mDependencyGraph;
    }
}