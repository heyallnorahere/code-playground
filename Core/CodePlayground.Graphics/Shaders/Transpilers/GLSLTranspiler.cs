using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CodePlayground.Graphics.Shaders.Transpilers
{
    internal struct TranslatedMethodInfo
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

    internal struct StructDependencyInfo
    {
        public HashSet<Type> Dependencies { get; set; }
        public HashSet<Type> Dependents { get; set; }
        public Dictionary<string, string> DefinedFields { get; set; }
    }

    internal struct StageIOField
    {
        public string Direction { get; set; }
        public int Location { get; set; }
        public string TypeName { get; set; }
    }

    internal enum ConditionalType
    {
        Unconditional,
        True,
        False
    }

    internal struct JumpCondition
    {
        public ConditionalType Type { get; set; }
        public string? Expression { get; set; }
    }

    internal struct JumpInstruction
    {
        public JumpCondition Condition { get; set; }
        public int Offset { get; set; }
        public int Destination { get; set; }
    }

    internal enum ScopeType
    {
        Loop,
        Conditional
    }

    internal sealed class Scope
    {
        public Scope(int startOffset, int byteLength, ScopeType type)
        {
            StartOffset = startOffset;
            ByteLength = byteLength;

            Type = type;
            Children = new List<Scope>();
            Parent = null;
        }

        public int StartOffset { get; }
        public int ByteLength { get; }
        public int EndOffset => StartOffset + ByteLength;

        public ScopeType Type { get; set; }
        public List<Scope> Children { get; }
        public Scope? Parent { get; set; }
    }

    internal sealed class SourceMapCollection
    {
        public SourceMapCollection(IReadOnlyList<ILInstruction> instructions)
        {
            SourceOffsets = new Dictionary<int, int>();
            OffsetInstructionMap = new Dictionary<int, int>();
            InstructionOffsets = instructions.Select(instruction => instruction.Offset).ToArray();
        }

        public Dictionary<int, int> SourceOffsets { get; }
        public Dictionary<int, int> OffsetInstructionMap { get; }
        public IReadOnlyList<int> InstructionOffsets { get; }
    }

    internal sealed class GLSLTranspiler : ShaderTranspiler
    {
        // shaderc.net does not pass your entrypoint name through at all, so...
        private const string EntrypointName = "main";

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
            mDependencyGraph = new Dictionary<MethodInfo, TranslatedMethodInfo>();
            mStructDependencies = new Dictionary<Type, StructDependencyInfo>();
            mMethodScopes = new List<Scope>();
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
            if (asType && !type.IsValueType && attribute is null)
            {
                throw new InvalidOperationException("Cannot use a non-value type as a shader type!");
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

        private void ParseReturnStructure(Type type, ICustomAttributeProvider provider, string identifierExpression, Action<Type, string, string, int> callback)
        {
            if (provider.GetCustomAttributes(typeof(OutputPositionAttribute), true).Length != 0)
            {
                callback.Invoke(type, identifierExpression, "gl_Position", -1);
                return;
            }

            var layoutAttributes = provider.GetCustomAttributes(typeof(LayoutAttribute), true);
            if (layoutAttributes.Length != 0)
            {
                var attribute = (LayoutAttribute)layoutAttributes[0];
                if (attribute.Location >= 0)
                {
                    string destination = identifierExpression.Replace('.', '_');
                    callback.Invoke(type, identifierExpression, destination, attribute.Location);
                    return;
                }
            }

            if (type.IsPrimitive)
            {
                return;
            }

            if (type.IsClass)
            {
                throw new InvalidOperationException("Cannot use a class as a return type!");
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldName = GetFieldName(field, typeof(void)); // type doesnt really matter
                ParseReturnStructure(field.FieldType, field, $"{identifierExpression}.{fieldName}", callback);
            }
        }

        private void ProcessType(Type type, Type shaderType)
        {
            if (mStructDependencies.ContainsKey(type) || !type.IsValueType || type.IsPrimitive)
            {
                return;
            }

            var dependencyInfo = new StructDependencyInfo
            {
                Dependencies = new HashSet<Type>(),
                Dependents = new HashSet<Type>(),
                DefinedFields = new Dictionary<string, string>()
            };

            // make sure that we don't get a stack overflow
            mStructDependencies.Add(type, dependencyInfo);

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldName = GetFieldName(field, shaderType);
                var fieldType = field.FieldType;
                var fieldTypeName = GetTypeName(fieldType, shaderType, true);

                dependencyInfo.DefinedFields.Add(fieldName, fieldTypeName);
                if (fieldType != type && fieldType.IsValueType && !fieldType.IsPrimitive)
                {
                    dependencyInfo.Dependencies.Add(fieldType);

                    ProcessType(fieldType, shaderType);
                    mStructDependencies[fieldType].Dependents.Add(type);
                }
            }
        }

        private static void PushOperatorExpression(ShaderOperatorType type, Stack<string> evaluationStack)
        {
            var rhs = evaluationStack.Pop();
            evaluationStack.Push(type switch
            {
                ShaderOperatorType.Add => $"({evaluationStack.Pop()} + {rhs})",
                ShaderOperatorType.Subtract => $"({evaluationStack.Pop()} - {rhs})",
                ShaderOperatorType.Multiply => $"({evaluationStack.Pop()} * {rhs})",
                ShaderOperatorType.Divide => $"({evaluationStack.Pop()} / {rhs})",
                ShaderOperatorType.Invert => $"(-{rhs})",
                _ => throw new ArgumentException("Invalid shader operator!")
            });
        }

        private TranslatedMethodInfo TranspileMethod(Type type, MethodInfo method, bool entrypoint)
        {
            var body = method.GetMethodBody();
            var instructions = body?.GetILAsInstructionList(method.Module);

            if (body is null || instructions is null)
            {
                throw new ArgumentException("Cannot transpile a method without a body!");
            }

            var parameterNames = new List<string>();
            var parameters = method.GetParameters();

            for (int i = 0; i < parameters.Length; i++)
            {
                var name = parameters[i].Name ?? $"param_{i}";
                parameterNames.Add(name);
            }

            string returnTypeString, parameterString, functionName;
            var outputFields = new Dictionary<string, string>();

            if (entrypoint)
            {
                returnTypeString = "void";
                parameterString = string.Empty;
                functionName = EntrypointName;

                var returnType = method.ReturnType;
                var attributes = method.ReturnTypeCustomAttributes;

                ParseReturnStructure(returnType, attributes, string.Empty, (fieldType, expression, destination, location) =>
                {
                    string outputName;
                    if (location >= 0)
                    {
                        outputName = "_output" + destination;
                        mStageIO.Add(outputName, new StageIOField
                        {
                            Direction = "out",
                            Location = location,
                            TypeName = GetTypeName(fieldType, type, true)
                        });
                    }
                    else
                    {
                        outputName = destination;
                    }

                    outputFields.Add(expression, outputName);
                    ProcessType(fieldType, type);
                });
            }
            else
            {
                returnTypeString = GetTypeName(method.ReturnType, type, true);
                parameterString = string.Empty;
                functionName = GetFunctionName(method, type);

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        parameterString += ", ";
                    }

                    var parameter = parameters[i];
                    var parameterType = parameter.ParameterType;
                    var parameterTypeName = GetTypeName(parameterType, type, true);

                    parameterString += $"{parameterTypeName} {parameterNames[i]}";
                    ProcessType(parameterType, type);
                }
            }

            var builder = new StringBuilder();
            builder.AppendLine($"{returnTypeString} {functionName}({parameterString}) {{");

            var localVariables = body.LocalVariables;
            for (int i = 0; i < localVariables.Count; i++)
            {
                var variableType = localVariables[i].LocalType;
                ProcessType(variableType, type);

                string variableTypeName = GetTypeName(variableType, type, true);
                builder.AppendLine($"{variableTypeName} var_{i};");
            }

            var evaluationStack = new Stack<string>();
            var dependencies = new List<MethodInfo>();
            var jumps = new List<JumpInstruction>();

            var mapCollection = new SourceMapCollection(instructions);
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];

                mapCollection.SourceOffsets.Add(instruction.Offset, builder.Length);
                mapCollection.OffsetInstructionMap.Add(instruction.Offset, i);

                var opCode = instruction.OpCode;
                var name = opCode.Name?.ToLower();

                if (string.IsNullOrEmpty(name) || name == "nop" || name == "break")
                {
                    continue;
                }

                // https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.opcodes?view=net-7.0
                if (name.StartsWith("call"))
                {
                    MethodInfo invokedMethod;
                    if (instruction.Operand is MethodInfo operand)
                    {
                        var operatorAttribute = operand.GetCustomAttribute<ShaderOperatorAttribute>();
                        if (operatorAttribute is not null)
                        {
                            PushOperatorExpression(operatorAttribute.Type, evaluationStack);
                            continue;
                        }

                        invokedMethod = operand;
                        if (operand != method)
                        {
                            dependencies.Add(operand);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Cannot dynamically call shader functions!");
                    }

                    var invocationParameters = invokedMethod.GetParameters();
                    var expressionString = CreateParameterExpressionString(evaluationStack, invocationParameters.Length);

                    if (!invokedMethod.IsStatic)
                    {
                        string invokedObject = evaluationStack.Pop();
                        if (invokedMethod.GetCustomAttribute<PrimitiveShaderTypeAttribute>() is null)
                        {
                            var declaringType = invokedMethod.DeclaringType;
                            if (declaringType is null || !type.Extends(declaringType))
                            {
                                throw new InvalidOperationException("Cannot call an instance method outside of the shader class!");
                            }
                        }
                        else
                        {
                            expressionString = $"{invokedObject}, {expressionString}";
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
                                    bool isInput = false;
                                    foreach (var parameterName in parameterNames)
                                    {
                                        if (expression.Length > parameterName.Length ? expression.StartsWith(parameterName) : expression == parameterName)
                                        {
                                            isInput = true;
                                            break;
                                        }
                                    }

                                    if (isInput)
                                    {
                                        expression = "_input_" + expression.Replace('.', '_');
                                        if (!mStageIO.ContainsKey(expression))
                                        {
                                            mStageIO.Add(expression, new StageIOField
                                            {
                                                Direction = "in",
                                                Location = layoutAttribute.Location,
                                                TypeName = GetTypeName(field.FieldType, type, true)
                                            });

                                            ProcessType(field.FieldType, type);
                                        }
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

                        if (argumentIndex < 0)
                        {
                            expression = "this";
                        }
                        else
                        {
                            expression = parameterNames[argumentIndex];
                            if (entrypoint)
                            {
                                var parameter = parameters[argumentIndex];
                                var parameterType = parameter.ParameterType;

                                var attribute = parameter.GetCustomAttribute<LayoutAttribute>();
                                if (attribute is not null && attribute.Location >= 0)
                                {
                                    expression = "_input_" + expression;
                                    if (!mStageIO.ContainsKey(expression))
                                    {
                                        mStageIO.Add(expression, new StageIOField
                                        {
                                            Direction = "in",
                                            Location = attribute.Location,
                                            TypeName = GetTypeName(parameterType, type, true)
                                        });

                                        ProcessType(parameterType, type);
                                    }
                                }
                            }
                        }
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
                        string? parsedExpression = instruction.Operand?.ToString();
                        if (parsedExpression is null)
                        {
                            int lastSeparator = name.LastIndexOf('.');
                            if (lastSeparator >= 0)
                            {
                                var valueSegment = name[(lastSeparator + 1)..];
                                if (int.TryParse(valueSegment, out int parsedInteger))
                                {
                                    parsedExpression = valueSegment;
                                }
                            }
                        }

                        expression = parsedExpression ?? throw new InvalidOperationException("Null values are not permitted in shaders!");
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

                        builder.AppendLine($"{array}[{index}] = {expression};");
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported store operation!");
                    }
                }
                else if (name.StartsWith("br"))
                {
                    var conditionalOp = name[2..];
                    var condition = new JumpCondition();

                    if (conditionalOp.StartsWith("false"))
                    {
                        condition.Type = ConditionalType.False;
                    }
                    else if (conditionalOp.StartsWith("true"))
                    {
                        condition.Type = ConditionalType.True;
                    }
                    else
                    {
                        condition.Type = ConditionalType.Unconditional;
                    }

                    condition.Expression = condition.Type != ConditionalType.Unconditional ? evaluationStack.Pop() : null;
                    jumps.Add(new JumpInstruction
                    {
                        Offset = instruction.Offset,
                        Destination = Convert.ToInt32(instruction.Operand!),
                        Condition = condition
                    });
                }
                else if (name.StartsWith("add"))
                {
                    PushOperatorExpression(ShaderOperatorType.Add, evaluationStack);
                }
                else if (name.StartsWith("sub"))
                {
                    PushOperatorExpression(ShaderOperatorType.Subtract, evaluationStack);
                }
                else if (name.StartsWith("mul"))
                {
                    PushOperatorExpression(ShaderOperatorType.Multiply, evaluationStack);
                }
                else if (name.StartsWith("div"))
                {
                    PushOperatorExpression(ShaderOperatorType.Divide, evaluationStack);
                }
                else if (name.StartsWith("neg"))
                {
                    PushOperatorExpression(ShaderOperatorType.Invert, evaluationStack);
                }
                else if (name.StartsWith("ceq"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"{lhs} == {rhs}");
                }
                else if (name.StartsWith("cgt"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"{lhs} > {rhs}");
                }
                else if (name.StartsWith("clt"))
                {
                    var rhs = evaluationStack.Pop();
                    var lhs = evaluationStack.Pop();

                    evaluationStack.Push($"{lhs} < {rhs}");
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
                                var declaringType = constructor.DeclaringType!;
                                string typeName = GetTypeName(declaringType, type, true);

                                var attribute = declaringType.GetCustomAttribute<PrimitiveShaderTypeAttribute>();
                                if (!attribute!.Instantiable)
                                {
                                    throw new InvalidOperationException($"Shader type \"{typeName}\" is not instantiable!");
                                }

                                var constructorParameters = constructor.GetParameters();
                                var expressionString = CreateParameterExpressionString(evaluationStack, constructorParameters.Length);

                                evaluationStack.Push($"{typeName}({expressionString})");
                            }
                            break;
                        case "ret":
                            {
                                var returnedExpression = evaluationStack.Pop();
                                if (entrypoint)
                                {
                                    foreach (var expression in outputFields.Keys)
                                    {
                                        var outputName = outputFields[expression];
                                        builder.AppendLine($"{outputName} = {returnedExpression}{expression};");
                                    }
                                }
                                else
                                {
                                    builder.AppendLine($"return {returnedExpression};");
                                }
                            }
                            break;
                        default:
                            throw new InvalidOperationException($"Instruction {name} has not been implemented yet!");
                    }
                }
            }

            var nonLoopJumps = new List<JumpInstruction>();
            foreach (var jump in jumps)
            {
                if (jump.Destination > jump.Offset)
                {
                    nonLoopJumps.Add(jump);
                    continue;
                }

                var scope = new Scope(jump.Destination, jump.Offset - jump.Destination, ScopeType.Loop);
                if (!AddScope(scope))
                {
                    throw new InvalidOperationException($"Jump at offset 0x{jump.Offset:X} is not a valid loop!");
                }

                string startCode, endCode;
                var condition = jump.Condition;

                if (condition.Type != ConditionalType.Unconditional)
                {
                    var expression = condition.Type != ConditionalType.True ? $"!({condition.Expression})" : condition.Expression;

                    startCode = "do {\n";
                    endCode = $"}} while ({expression});\n";
                }
                else
                {
                    startCode = "while (true) {\n";
                    endCode = "}\n";
                }

                int instructionIndex = mapCollection.OffsetInstructionMap[jump.Offset];
                var nextInstruction = mapCollection.InstructionOffsets[instructionIndex + 1];

                InsertCode(jump.Destination, startCode, builder, mapCollection);
                InsertCode(nextInstruction, endCode, builder, mapCollection);
            }

            foreach (var jump in nonLoopJumps)
            {
                var condition = jump.Condition;
                var containingScope = FindContainingScope(jump.Offset);
                if (containingScope is not null && jump.Destination > containingScope.EndOffset)
                {
                    var currentScope = containingScope;
                    while (currentScope.Parent is not null && currentScope.Type != ScopeType.Loop)
                    {
                        currentScope = currentScope.Parent;
                    }

                    if (currentScope.Type == ScopeType.Loop && jump.Destination > currentScope.EndOffset)
                    {
                        var currentParent = currentScope.Parent;
                        if (currentParent is not null && jump.Destination > currentParent.EndOffset)
                        {
                            throw new InvalidOperationException("Invalid break statement!");
                        }

                        string code = "break;\n";
                        if (condition.Type != ConditionalType.Unconditional)
                        {
                            var expression = condition.Type != ConditionalType.False ? condition.Expression : $"!({condition.Expression})";
                            code = $"if ({expression}) {{\n{code}}}\n";
                        }

                        InsertCode(jump.Offset, code, builder, mapCollection);
                        continue;
                    }

                    if (condition.Type != ConditionalType.Unconditional)
                    {
                        throw new InvalidOperationException("\"Else\" jumps may not have conditions!");
                    }

                    var containingParent = containingScope.Parent;
                    if (containingParent is not null && jump.Destination > containingParent.EndOffset)
                    {
                        throw new InvalidOperationException("Invalid else clause!");
                    }

                    var elseScope = new Scope(containingScope.EndOffset, jump.Destination - containingScope.EndOffset, ScopeType.Conditional);
                    if (!AddScope(elseScope))
                    {
                        throw new InvalidOperationException("Invalid scope generated for else clause!");
                    }

                    InsertCode(elseScope.StartOffset, " else {", builder, mapCollection, -1);
                    InsertCode(elseScope.EndOffset, "}\n", builder, mapCollection);

                    continue;
                }

                int instructionIndex = mapCollection.OffsetInstructionMap[jump.Offset];
                var nextInstruction = mapCollection.InstructionOffsets[instructionIndex + 1];

                if (condition.Type == ConditionalType.Unconditional)
                {
                    var comment = $"// skip to offset 0x{jump.Destination:X}... not sure how thats possible here\n";
                    InsertCode(nextInstruction, comment, builder, mapCollection);

                    continue;
                }

                var ifScope = new Scope(jump.Offset, jump.Destination - jump.Offset, ScopeType.Conditional);
                if (!AddScope(ifScope))
                {
                    throw new InvalidOperationException("Invalid scope generated for if statement!");
                }

                var ifExpression = condition.Type != ConditionalType.True ? condition.Expression : $"!({condition.Expression})";
                var startCode = $"if ({ifExpression}) {{\n";

                InsertCode(nextInstruction, startCode, builder, mapCollection);
                InsertCode(jump.Destination, "}\n", builder, mapCollection);
            }

            builder.AppendLine("}");
            mMethodScopes.Clear();

            return new TranslatedMethodInfo
            {
                Source = builder.ToString(),
                Dependencies = dependencies,
                IsBuiltin = false
            };
        }

        private bool AddScope(Scope scope)
        {
            if (scope.Parent is not null)
            {
                throw new InvalidOperationException("Scope already has a parent!");
            }

            var containingScope = FindContainingScope(scope.StartOffset);
            if (containingScope is not null && scope.EndOffset > containingScope.EndOffset)
            {
                return false;
            }

            var childList = containingScope?.Children ?? mMethodScopes;
            childList.Add(scope);

            scope.Parent = containingScope;
            return true;
        }

        private Scope? FindContainingScope(int offset)
        {
            Scope? currentScope = null;
            var childList = mMethodScopes;

            while (childList.Count > 0)
            {
                Scope? foundScope = null;
                foreach (var loop in childList)
                {
                    if (loop.StartOffset > offset)
                    {
                        continue;
                    }

                    if (offset < loop.EndOffset)
                    {
                        foundScope = loop;
                        break;
                    }
                }

                if (foundScope is not null)
                {
                    currentScope = foundScope;
                    childList = foundScope.Children;
                }
                else
                {
                    break;
                }
            }

            return currentScope;
        }

        private static void InsertCode(int instructionOffset, string code, StringBuilder source, SourceMapCollection mapCollection, int sourceOffset = 0)
        {
            int insertOffset = mapCollection.SourceOffsets[instructionOffset];
            source.Insert(insertOffset + sourceOffset, code);

            int index = mapCollection.OffsetInstructionMap[instructionOffset];
            for (int i = index; i < mapCollection.InstructionOffsets.Count; i++)
            {
                int offset = mapCollection.InstructionOffsets[i];
                mapCollection.SourceOffsets[offset] += code.Length;
            }
        }

        private void ClearData()
        {
            mDefinedTypeNames.Clear();
            mFunctionNames.Clear();
            mFieldNames.Clear();
            mStageIO.Clear();
            mDependencyGraph.Clear();
            mStructDependencies.Clear();
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
                mDependencyGraph.Add(method, new TranslatedMethodInfo
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

        private IReadOnlyList<Type> ResolveStructOrder()
        {
            var definedStructs = mStructDependencies.Keys.ToList();
            definedStructs.Sort((lhs, rhs) =>
            {
                var lhsDependencyCount = mStructDependencies[lhs].Dependencies.Count;
                var rhsDependencyCount = mStructDependencies[rhs].Dependencies.Count;

                return lhsDependencyCount.CompareTo(rhsDependencyCount);
            });

            var structOrder = new List<Type>();
            if (definedStructs.Count != 0)
            {
                IEnumerable<Type> evaluationList = new Type[]
                {
                    definedStructs[0]
                };

                while (evaluationList.Count() > 0)
                {
                    var newEvaluationList = new HashSet<Type>();
                    foreach (var type in evaluationList)
                    {
                        var info = mStructDependencies[type];

                        bool hasDependencies = true;
                        foreach (var dependency in info.Dependencies)
                        {
                            if (!structOrder.Contains(dependency))
                            {
                                hasDependencies = false;
                                newEvaluationList.Add(dependency);
                            }
                        }

                        if (!hasDependencies)
                        {
                            break;
                        }

                        structOrder.Add(type);
                        foreach (var dependent in info.Dependents)
                        {
                            newEvaluationList.Add(dependent);
                        }
                    }

                    evaluationList = newEvaluationList;
                }
            }

            return structOrder;
        }

        private IReadOnlyList<MethodInfo> ResolveFunctionOrder()
        {
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

            var functionOrder = new List<MethodInfo>();
            while (evaluationList.Count() > 0)
            {
                var newEvaluationList = new HashSet<MethodInfo>();
                foreach (var method in evaluationList)
                {
                    var info = dependencyInfo[dependencyInfoIndices[method]];

                    bool hasDependencies = true;
                    foreach (var dependency in info.Dependencies)
                    {
                        if (!functionOrder.Contains(dependency))
                        {
                            hasDependencies = false;
                            newEvaluationList.Add(dependency);
                        }
                    }

                    if (!hasDependencies)
                    {
                        break;
                    }

                    functionOrder.Add(method);
                    foreach (var dependent in info.Dependents)
                    {
                        newEvaluationList.Add(dependent);
                    }
                }

                evaluationList = newEvaluationList;
            }

            return functionOrder;
        }

        protected override StageOutput TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage)
        {
            ProcessMethod(type, entrypoint, true);

            var builder = new StringBuilder();
            builder.AppendLine("#version 450\n");

            var structOrder = ResolveStructOrder();
            foreach (var definedStruct in structOrder)
            {
                var info = mStructDependencies[definedStruct];
                var name = GetTypeName(definedStruct, type, true);

                builder.AppendLine($"struct {name} {{");
                foreach (var fieldName in info.DefinedFields.Keys)
                {
                    var fieldType = info.DefinedFields[fieldName];
                    builder.AppendLine($"{fieldType} {fieldName};");
                }

                builder.AppendLine("};\n");
            }

            foreach (string fieldName in mStageIO.Keys)
            {
                var fieldData = mStageIO[fieldName];
                builder.AppendLine($"layout(location = {fieldData.Location}) {fieldData.Direction} {fieldData.TypeName} {fieldName};");
            }

            var functionOrder = ResolveFunctionOrder();
            foreach (var method in functionOrder)
            {
                var source = mDependencyGraph[method].Source;
                builder.AppendLine(source);
            }

            ClearData();
            return new StageOutput
            {
                Source = builder.ToString(),
                Entrypoint = EntrypointName
            };
        }

        private readonly Dictionary<Type, string> mDefinedTypeNames;
        private readonly Dictionary<MethodInfo, string> mFunctionNames;
        private readonly Dictionary<FieldInfo, string> mFieldNames;
        private readonly Dictionary<string, StageIOField> mStageIO;
        private readonly Dictionary<MethodInfo, TranslatedMethodInfo> mDependencyGraph;
        private readonly Dictionary<Type, StructDependencyInfo> mStructDependencies;
        private readonly List<Scope> mMethodScopes;
    }
}