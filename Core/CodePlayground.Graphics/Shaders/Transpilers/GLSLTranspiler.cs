using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CodePlayground.Graphics.Shaders.Transpilers
{
    internal sealed class GLSLTranspiler : ShaderTranspiler
    {
        // shaderc doesn't actually take your entrypoint name into account...
        private const string EntrypointName = "main";

        private static readonly IReadOnlyDictionary<Type, string> sPrimitiveTypeNames;
        private static readonly IReadOnlyDictionary<ShaderVariableID, string> sShaderVariableNames;
        private static readonly IReadOnlyDictionary<string, string> sConversionTypes;

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

            sShaderVariableNames = new Dictionary<ShaderVariableID, string>
            {
                // vertex shader
                [ShaderVariableID.OutputPosition] = "gl_Position",

                // fragment shader
                [ShaderVariableID.FragmentDepth] = "gl_FragDepth",

                // geometry shader
                [ShaderVariableID.CubemapLayer] = "gl_Layer",
                [ShaderVariableID.GeometryInput] = "gl_in",

                // compute shader
                [ShaderVariableID.WorkGroupCount] = "gl_NumWorkGroups",
                [ShaderVariableID.WorkGroupID] = "gl_WorkGroupID",
                [ShaderVariableID.WorkGroupSize] = "gl_WorkGroupSize", // compile-time constant
                [ShaderVariableID.LocalInvocationID] = "gl_LocalInvocationID",
                [ShaderVariableID.GlobalInvocationID] = "gl_GlobalInvocationID",
                [ShaderVariableID.LocalInvocationIndex] = "gl_LocalInvocationIndex",
            };

            sConversionTypes = new Dictionary<string, string>
            {
                ["r"] = "float",
                ["r4"] = "float",
                ["r8"] = "double",
                ["i4"] = "int",
                ["u4"] = "uint"
            };
        }

        public GLSLTranspiler()
        {
            mDefinedTypeNames = new Dictionary<Type, string>();
            mFunctionNames = new Dictionary<MethodInfo, string>();
            mFieldNames = new Dictionary<FieldInfo, string>();
            mStageIO = new Dictionary<string, StageIOField>();
            mStageResources = new Dictionary<string, ShaderResource>();
            mSharedVariables = new Dictionary<string, Type>();
            mDependencyGraph = new Dictionary<MethodInfo, TranslatedMethodInfo>();
            mStructDependencies = new Dictionary<Type, StructDependencyInfo>();
            mMethodScopes = new List<Scope>();
        }

        private string? GetMemberName(MemberInfo info)
        {
            var attributes = info.GetCustomAttributes<NamedShaderSymbolAttribute>();
            foreach (var attribute in attributes)
            {
                var language = attribute.Language;
                if (language == ShaderLanguage.None || language == OutputLanguage)
                {
                    return attribute.Name;
                }
            }

            return null;
        }

        private string GetFieldName(FieldInfo field, Type shaderType)
        {
            using var fieldNameEvent = Profiler.Event();

            if (mFieldNames.ContainsKey(field))
            {
                return mFieldNames[field];
            }

            var definedName = GetMemberName(field);
            var name = definedName ?? field.Name;

            if (definedName is null)
            {
                var declaringType = field.DeclaringType;
                if (field.IsStatic && declaringType is not null && declaringType != shaderType)
                {
                    string typeName = GetTypeName(declaringType, shaderType, false);
                    name = $"{typeName}_{name}";
                }
            }

            mFieldNames.Add(field, name);
            return name;
        }

        private string GetFunctionName(MethodInfo method, Type shaderType)
        {
            using var functionNameEvent = Profiler.Event();

            if (mFunctionNames.ContainsKey(method))
            {
                return mFunctionNames[method];
            }

            var attribute = method.GetCustomAttribute<BuiltinShaderFunctionAttribute>();
            if (attribute is not null)
            {
                var definedName = GetMemberName(method);
                var result = definedName ?? (method.Name[..1].ToLower() + method.Name[1..]);

                mFunctionNames.Add(method, result);
                return result;
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
                    glslName += "_";
                }

                glslName += nameList[i];
            }

            mFunctionNames.Add(method, glslName);
            return glslName;
        }

        private string GetTypeName(Type type, Type shaderType, bool asType)
        {
            using var typeNameEvent = Profiler.Event();

            if (sPrimitiveTypeNames.ContainsKey(type))
            {
                return sPrimitiveTypeNames[type];
            }

            var attribute = type.GetCustomAttribute<PrimitiveShaderTypeAttribute>();
            if (asType && !type.IsValueType && attribute is null)
            {
                throw new InvalidOperationException("Cannot use a non-value type as a shader type!");
            }

            if (mDefinedTypeNames.TryGetValue(type, out string? typeName))
            {
                return typeName;
            }

            if (type.IsEnum)
            {
                string underlyingName = GetTypeName(type.GetEnumUnderlyingType(), shaderType, true);
                mDefinedTypeNames.Add(type, underlyingName);

                return underlyingName;
            }

            if (attribute is not null)
            {
                var primitiveName = GetMemberName(type);
                if (primitiveName is null)
                {
                    throw new ArgumentException($"No primitive name defined for type: {type}");
                }

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

                    if (argument != typeof(float))
                    {
                        primitiveName = primitiveName.Insert(0, sPrimitiveTypeNames[argument][0..1]);
                    }
                }

                mDefinedTypeNames.Add(type, primitiveName);
                return primitiveName;
            }
            else if (GetMemberName(type) is not null)
            {
                throw new ArgumentException("Cannot define a name for a non-primitive type!");
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

        private string CreateParameterExpressionString(Stack<string> evaluationStack, Type shaderType, IReadOnlyList<ParameterInfo> parameters)
        {
            using var parameterExpressionStringEvent = Profiler.Event();

            string expressionString = string.Empty;
            for (int i = parameters.Count - 1; i >= 0; i--)
            {
                string parameterExpression = evaluationStack.Pop();
                var parameterType = parameters[i].ParameterType;

                if (parameterType.IsPrimitive)
                {
                    var parameterTypeName = GetTypeName(parameterType, shaderType, true);
                    parameterExpression = $"{parameterTypeName}({parameterExpression})";
                }

                string parameterSeparator = i > 0 ? ", " : string.Empty;
                var currentParameter = parameterSeparator + parameterExpression;
                expressionString = currentParameter + expressionString;
            }

            return expressionString;
        }

        /// <summary>
        /// Parse the signature of an entrypoint function for resource declaration
        /// </summary>
        /// <param name="type">The type of parameter/element/return value</param>
        /// <param name="provider">Attribute provider of said element, usually a <see cref="ParameterInfo"/> or <see cref="FieldInfo"/> object</param>
        /// <param name="identifierExpression">The current "built" expression</param>
        /// <param name="callback">Function to call for every element parsed</param>
        private void ParseSignatureStructure(Type type, ICustomAttributeProvider provider, string identifierExpression, Action<Type, string, string, int, bool> callback)
        {
            using var parseEvent = Profiler.Event();

            if (type == typeof(void))
            {
                return;
            }

            var shaderVariableAttributes = provider.GetCustomAttributes(typeof(ShaderVariableAttribute), true);
            if (shaderVariableAttributes.Length != 0)
            {
                var attribute = (ShaderVariableAttribute)shaderVariableAttributes[0];
                var id = attribute.ID;

                if (!sShaderVariableNames.TryGetValue(id, out string? variableName))
                {
                    throw new ArgumentException($"Invalid GLSL shader variable: {id}");
                }

                callback.Invoke(type, identifierExpression, variableName, -1, false);
                return;
            }

            var layoutAttributes = provider.GetCustomAttributes(typeof(LayoutAttribute), true);
            if (layoutAttributes.Length != 0)
            {
                var attribute = (LayoutAttribute)layoutAttributes[0];
                if (attribute.Location >= 0)
                {
                    string destination = identifierExpression.Replace('.', '_');
                    callback.Invoke(type, identifierExpression, destination, attribute.Location, attribute.Flat);
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
                ParseSignatureStructure(field.FieldType, field, $"{identifierExpression}.{fieldName}", callback);
            }
        }

        /// <summary>
        /// Defines a type to be inserted into GLSL code after methods have finished processing
        /// </summary>
        /// <param name="type">Type to process</param>
        /// <param name="shaderType">Type of the shader that's processing</param>
        /// <param name="defineStruct">Whether or not to actually define this (e.g. buffer type)</param>
        private void ProcessType(Type type, Type shaderType, bool defineStruct = true)
        {
            using var processTypeEvent = Profiler.Event();

            if (mStructDependencies.ContainsKey(type) || !type.IsValueType || type.IsPrimitive || type.IsEnum)
            {
                return;
            }

            var dependencyInfo = new StructDependencyInfo
            {
                Dependencies = new HashSet<Type>(),
                Dependents = new HashSet<Type>(),
                DefinedFields = new Dictionary<string, StructFieldInfo>(),
                Define = defineStruct
            };

            // make sure that we don't get a stack overflow
            mStructDependencies.Add(type, dependencyInfo);

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldName = GetFieldName(field, shaderType);
                var fieldType = field.FieldType;

                Type elementType;
                int arraySize;
                if (fieldType.IsArray)
                {
                    elementType = fieldType.GetElementType() ?? throw new InvalidOperationException("Invalid array type!");
                    arraySize = (int?)field.GetCustomAttribute<ArraySizeAttribute>()?.Length ?? -1;
                }
                else
                {
                    elementType = fieldType;
                    arraySize = 0;
                }

                var fieldTypeName = GetTypeName(elementType, shaderType, true);
                dependencyInfo.DefinedFields.Add(fieldName, new StructFieldInfo
                {
                    TypeName = fieldTypeName,
                    ArraySize = arraySize
                });

                if (elementType != type && elementType.IsValueType && !elementType.IsPrimitive && !elementType.IsEnum)
                {
                    dependencyInfo.Dependencies.Add(elementType);

                    ProcessType(elementType, shaderType);
                    mStructDependencies[elementType].Dependents.Add(type);
                }
            }
        }

        /// <summary>
        /// Pops operands from the evaluation stack, and pushes back an operation expression
        /// </summary>
        /// <param name="type">The type of operation to perform</param>
        /// <param name="evaluationStack">The stack on which to operate</param>
        private static void PushOperatorExpression(ShaderOperatorType type, Stack<string> evaluationStack)
        {
            using var pushEvent = Profiler.Event();

            var rhs = evaluationStack.Pop();
            if (type == ShaderOperatorType.Not)
            {
                evaluationStack.Push(rhs);
                evaluationStack.Push("0");

                // hack
                PushOperatorExpression(ShaderOperatorType.Equal, evaluationStack);
                return;
            }

            if (type == ShaderOperatorType.Equal || type == ShaderOperatorType.NotEqual)
            {
                var lhs = evaluationStack.Pop();
                var operatorExpression = type switch
                {
                    ShaderOperatorType.Equal => "==",
                    ShaderOperatorType.NotEqual => "!=",
                    _ => throw new ArgumentException("what")
                };

                // hack
                if ((lhs.Contains('<') || lhs.Contains('>') || lhs.Contains('=')) && int.TryParse(rhs, out int result))
                {
                    // HACK
                    rhs = (result != 0).ToString().ToLower();
                }

                evaluationStack.Push($"({lhs} {operatorExpression} {rhs})");
                return;
            }

            evaluationStack.Push(type switch
            {
                ShaderOperatorType.Add => $"({evaluationStack.Pop()} + {rhs})",
                ShaderOperatorType.Subtract => $"({evaluationStack.Pop()} - {rhs})",
                ShaderOperatorType.Multiply => $"({evaluationStack.Pop()} * {rhs})",
                ShaderOperatorType.Divide => $"({evaluationStack.Pop()} / {rhs})",
                ShaderOperatorType.Invert => $"(-{rhs})",
                ShaderOperatorType.And => $"({evaluationStack.Pop()} & {rhs})",
                ShaderOperatorType.Or => $"({evaluationStack.Pop()} | {rhs})",
                ShaderOperatorType.ShiftLeft => $"({evaluationStack.Pop()} << {rhs})",
                ShaderOperatorType.ShiftRight => $"({evaluationStack.Pop()} >> {rhs})",
                ShaderOperatorType.Index => $"{evaluationStack.Pop()}[{rhs}]",

                ShaderOperatorType.GreaterEqual => $"({evaluationStack.Pop()} >= {rhs})",
                ShaderOperatorType.Greater => $"({evaluationStack.Pop()} > {rhs})",
                ShaderOperatorType.LessEqual => $"({evaluationStack.Pop()} <= {rhs})",
                ShaderOperatorType.Less => $"({evaluationStack.Pop()} < {rhs})",

                _ => throw new ArgumentException("Invalid shader operator!")
            });
        }

        private TranslatedMethodInfo TranspileMethod(Type type, MethodInfo method, bool entrypoint)
        {
            using var transpileEvent = Profiler.Event();

            var body = method.GetMethodBody();
            var instructions = body?.GetILAsInstructionList(method.Module);

            if (body is null || instructions is null)
            {
                throw new ArgumentException("Cannot transpile a method without a body!");
            }

            var parameterNames = new List<string>();
            var parameters = method.GetParameters();

            string returnTypeString, parameterString, functionName;
            var inputFields = new Dictionary<string, string>();
            var outputFields = new Dictionary<string, string>();

            using (Profiler.Event("Parse shader function signature"))
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];

                    var name = parameter.Name ?? $"param_{i}";
                    parameterNames.Add(name);

                    if (entrypoint)
                    {
                        ParseSignatureStructure(parameter.ParameterType, parameter, name, (fieldType, expression, destination, location, flat) =>
                        {
                            string inputName;
                            if (location >= 0)
                            {
                                inputName = "_input_" + destination;
                                mStageIO.Add(inputName, new StageIOField
                                {
                                    Direction = StageIODirection.In,
                                    Location = location,
                                    TypeName = GetTypeName(fieldType, type, true),
                                    Flat = flat
                                });
                            }
                            else
                            {
                                inputName = destination; // builtin shader variable (e.g. gl_LocalInvocationID)
                            }

                            inputFields.Add(expression, inputName);
                            ProcessType(fieldType, type);
                        });
                    }
                }

                if (entrypoint)
                {
                    returnTypeString = "void";
                    parameterString = string.Empty;
                    functionName = EntrypointName;

                    var outputType = method.ReturnType;
                    var outputAttributes = method.ReturnTypeCustomAttributes;

                    ParseSignatureStructure(outputType, outputAttributes, string.Empty, (fieldType, expression, destination, location, flat) =>
                    {
                        string outputName;
                        if (location >= 0)
                        {
                            outputName = "_output" + destination;
                            mStageIO.Add(outputName, new StageIOField
                            {
                                Direction = StageIODirection.Out,
                                Location = location,
                                TypeName = GetTypeName(fieldType, type, true),
                                Flat = flat
                            });
                        }
                        else
                        {
                            outputName = destination; // builtin e.g. gl_Position
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
            }

            var builder = new StringBuilder();
            builder.AppendLine($"{returnTypeString} {functionName}({parameterString}) {{");

            var dependencies = new List<MethodInfo>();
            using (Profiler.Event("Parse shader function body"))
            {
                var localVariables = body.LocalVariables;
                for (int i = 0; i < localVariables.Count; i++)
                {
                    var variableType = localVariables[i].LocalType;
                    ProcessType(variableType, type);

                    string variableTypeName = GetTypeName(variableType, type, true);
                    builder.AppendLine($"{variableTypeName} var_{i};");
                }

                var evaluationStack = new Stack<string>();
                var jumps = new List<JumpInstruction>();

                var mapCollection = new SourceMapCollection(instructions);
                using (Profiler.Event("Parse shader IL"))
                {
                    // slurrrrp... tasty, tasty spaghetti...
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
                        if (name.StartsWith("call")) // method invocation
                        {
                            MethodInfo invokedMethod;
                            if (instruction.Operand is MethodInfo operand)
                            {
                                var operatorAttribute = operand.GetCustomAttribute<ShaderOperatorAttribute>();
                                if (operatorAttribute is not null) // method is an overloaded operator
                                {
                                    string? setValue = null;
                                    if (operatorAttribute.Type == ShaderOperatorType.Index && operand.GetParameters().Length > 1)
                                    {
                                        setValue = evaluationStack.Pop();
                                    }

                                    PushOperatorExpression(operatorAttribute.Type, evaluationStack);
                                    if (setValue is not null)
                                    {
                                        var target = evaluationStack.Pop();
                                        builder.AppendLine($"{target} = {setValue};");
                                    }

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
                            var expressionString = CreateParameterExpressionString(evaluationStack, type, invocationParameters);

                            if (!invokedMethod.IsStatic)
                            {
                                string invokedObject = evaluationStack.Pop();
                                if (invokedMethod.GetCustomAttribute<BuiltinShaderFunctionAttribute>() is null)
                                {
                                    var declaringType = invokedMethod.DeclaringType;
                                    if (declaringType is null || !type.Extends(declaringType))
                                    {
                                        throw new InvalidOperationException("Cannot call an instance method outside of the shader class!");
                                    }
                                }
                                else
                                {
                                    var existingExpressions = expressionString;
                                    expressionString = invokedObject;

                                    if (existingExpressions.Length > 0)
                                    {
                                        expressionString += $", {existingExpressions}";
                                    }
                                }
                            }

                            bool isKeyword = invokedMethod.GetCustomAttribute<BuiltinShaderFunctionAttribute>()?.Keyword ?? false;
                            string invokedFunctionName = GetFunctionName(invokedMethod, type);
                            string invocationExpression = isKeyword ? invokedFunctionName : $"{invokedFunctionName}({expressionString})";

                            if (invokedMethod.ReturnType != typeof(void))
                            {
                                evaluationStack.Push(invocationExpression);
                            }
                            else
                            {
                                builder.AppendLine($"{invocationExpression};");
                            }
                        }
                        else if (name.StartsWith("ld")) // load value
                        {
                            string loadType = name[2..];
                            if (!loadType.StartsWith("ind")) // if were not loading from a pointer (if we are we dont need to modify the stack at all)
                            {
                                string expression;
                                if (instruction.Operand is FieldInfo field) // loading from a field
                                {
                                    var fieldName = GetFieldName(field, type);
                                    var layoutAttribute = field.GetCustomAttribute<LayoutAttribute>();

                                    if (field.DeclaringType == type || field.IsStatic) // loading a resource/shader input
                                    {
                                        // todo(nora): abstract into member method. we dont want to repeat this one snippet of code over and over again
                                        var fieldType = field.FieldType;
                                        if (layoutAttribute is null)
                                        {
                                            throw new InvalidOperationException("Static and/or shader fields must have the Layout attribute applied!");
                                        }
                                        else if (layoutAttribute.Location >= 0)
                                        {
                                            mStageIO.TryAdd(fieldName, new StageIOField
                                            {
                                                Direction = StageIODirection.In,
                                                Location = layoutAttribute.Location,
                                                TypeName = GetTypeName(field.FieldType, type, true),
                                                Flat = layoutAttribute.Flat
                                            });
                                        }
                                        else if (layoutAttribute.Shared) // declaring a shared variable
                                        {
                                            ProcessType(fieldType, type);

                                            if (!mSharedVariables.ContainsKey(fieldName))
                                            {
                                                mSharedVariables.Add(fieldName, fieldType);
                                            }
                                        }
                                        else if (!mStageResources.ContainsKey(fieldName)) // declaring a stage resource
                                        {
                                            int arraySize = -1;
                                            if (fieldType.IsArray)
                                            {
                                                var arraySizeAttribute = field.GetCustomAttribute<ArraySizeAttribute>();
                                                if (arraySizeAttribute is null)
                                                {
                                                    throw new InvalidOperationException("Implicitly-sized resource arrays are not permitted!");
                                                }

                                                arraySize = (int)arraySizeAttribute.Length;
                                                fieldType = fieldType.GetElementType() ?? throw new InvalidOperationException("Invalid array type!");
                                            }

                                            ProcessType(fieldType, type, false);

                                            string layoutString;
                                            if (layoutAttribute.PushConstant)
                                            {
                                                layoutString = "push_constant";
                                            }
                                            else
                                            {
                                                layoutString = $"set = {layoutAttribute.Set}, binding = {layoutAttribute.Binding}";

                                                var primitiveTypeAttribute = fieldType.GetCustomAttribute<PrimitiveShaderTypeAttribute>();
                                                if (primitiveTypeAttribute is null || primitiveTypeAttribute.TypeClass == PrimitiveShaderTypeClass.Value)
                                                {
                                                    layoutString = "std140, " + layoutString;
                                                }
                                                else if (primitiveTypeAttribute.TypeClass == PrimitiveShaderTypeClass.Image)
                                                {
                                                    var format = layoutAttribute.Format;
                                                    var formatEnumName = format.ToString();

                                                    var formatField = typeof(ShaderImageFormat).GetField(formatEnumName, BindingFlags.Static | BindingFlags.Public);
                                                    var definedFieldName = formatField is null ? null : GetMemberName(formatField);

                                                    string formatString = definedFieldName ?? formatEnumName.ToLower();
                                                    layoutString += ", " + formatString;
                                                }
                                            }

                                            mStageResources.Add(fieldName, new ShaderResource
                                            {
                                                Layout = layoutString,
                                                ResourceType = fieldType,
                                                Type = layoutAttribute.ResourceType,
                                                ArraySize = arraySize
                                            });
                                        }
                                    }

                                    if (!field.IsStatic)
                                    {
                                        var parentExpression = evaluationStack.Pop();
                                        expression = parentExpression != "this" ? $"{parentExpression}.{fieldName}" : fieldName;

                                        if (inputFields.TryGetValue(expression, out string? inputName))
                                        {
                                            expression = inputName;
                                        }
                                        else if (parentExpression != "this")
                                        {
                                            var attribute = field.GetCustomAttribute<ShaderVariableAttribute>();
                                            if (attribute is not null)
                                            {
                                                expression = $"{parentExpression}.{sShaderVariableNames[attribute.ID]}";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        expression = fieldName;
                                    }
                                }
                                else if (loadType.StartsWith("loc")) // loading a local variable
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
                                else if (loadType.StartsWith("arg")) // loading an argument
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
                                        if (inputFields.TryGetValue(expression, out string? inputName))
                                        {
                                            expression = inputName;
                                        }
                                    }
                                }
                                else if (loadType.StartsWith("elem")) // loading an array index
                                {
                                    var index = evaluationStack.Pop();
                                    var array = evaluationStack.Pop();
                                    expression = $"{array}[{index}]";
                                }
                                else if (instruction.Operand is string)
                                {
                                    throw new InvalidOperationException("Strings are not permitted in shaders!");
                                }
                                else // loading a raw value
                                {
                                    // note(nora): DISGUSTANG
                                    string? parsedExpression = instruction.Operand?.ToString();
                                    if (parsedExpression is null)
                                    {
                                        int lastSeparator = name.LastIndexOf('.');
                                        if (lastSeparator >= 0)
                                        {
                                            var valueSegment = name[(lastSeparator + 1)..];

                                            int factor = 1;
                                            if (valueSegment.StartsWith('m'))
                                            {
                                                factor *= -1;
                                                valueSegment = valueSegment[1..];
                                            }

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
                        }
                        else if (name.StartsWith("st")) // store value
                        {
                            var storeType = name[2..];
                            if (instruction.Operand is FieldInfo field) // storing in a field
                            {
                                var expression = evaluationStack.Pop();
                                var fieldType = field.FieldType;

                                // note(nora): this is gross
                                if (fieldType.IsPrimitive)
                                {
                                    var fieldTypeName = GetTypeName(fieldType, type, true);
                                    expression = $"{fieldTypeName}({expression})";
                                }

                                string destination = GetFieldName(field, type);
                                if (!field.IsStatic) // non-static field
                                {
                                    var destinationObject = evaluationStack.Pop();
                                    if (destinationObject != "this")
                                    {
                                        destination = $"{destinationObject}.{destination}";
                                    }
                                }
                                else
                                {
                                    var shaderVariableAttribute = field.GetCustomAttribute<ShaderVariableAttribute>();
                                    if (shaderVariableAttribute is not null)
                                    {
                                        destination = sShaderVariableNames[shaderVariableAttribute.ID];
                                    }
                                    else
                                    {
                                        var layoutAttribute = field.GetCustomAttribute<LayoutAttribute>();
                                        if (layoutAttribute?.Location >= 0 && !mStageIO.ContainsKey(destination))
                                        {
                                            mStageIO.Add(destination, new StageIOField
                                            {
                                                Direction = StageIODirection.Out,
                                                Location = layoutAttribute.Location,
                                                TypeName = GetTypeName(fieldType, type, true),
                                                Flat = layoutAttribute.Flat
                                            });
                                        }
                                    }
                                }

                                builder.AppendLine($"{destination} = {expression};");
                            }
                            else if (storeType.StartsWith("loc")) // local (scope) variable
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
                                var variableType = localVariables[variableIndex].LocalType;

                                // note(nora): also disgusting
                                if (variableType.IsPrimitive)
                                {
                                    var variableTypeName = GetTypeName(variableType, type, true);
                                    expression = $"{variableTypeName}({expression})";
                                }

                                builder.AppendLine($"var_{variableIndex} = {expression};");
                            }
                            else if (storeType.StartsWith("elem")) // array element
                            {
                                var expression = evaluationStack.Pop();
                                var index = evaluationStack.Pop();
                                var array = evaluationStack.Pop();

                                builder.AppendLine($"{array}[{index}] = {expression};");
                            }
                            else if (storeType.StartsWith("ind")) // storing to a pointer
                            {
                                var value = evaluationStack.Pop();
                                var address = evaluationStack.Pop();

                                builder.AppendLine($"{address} = {value};");
                            }
                            else
                            {
                                throw new InvalidOperationException("Unsupported store operation!");
                            }
                        }
                        else if (name.StartsWith("br")) // conditional/unconditional jump
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
                        else if (name.StartsWith("add")) // arithmetic operations; x + y
                        {
                            PushOperatorExpression(ShaderOperatorType.Add, evaluationStack);
                        }
                        else if (name.StartsWith("sub")) // x - y
                        {
                            PushOperatorExpression(ShaderOperatorType.Subtract, evaluationStack);
                        }
                        else if (name.StartsWith("mul")) // x * y
                        {
                            PushOperatorExpression(ShaderOperatorType.Multiply, evaluationStack);
                        }
                        else if (name.StartsWith("div")) // x / y
                        {
                            PushOperatorExpression(ShaderOperatorType.Divide, evaluationStack);
                        }
                        else if (name.StartsWith("neg")) // -x
                        {
                            PushOperatorExpression(ShaderOperatorType.Invert, evaluationStack);
                        }
                        else if (name.StartsWith("and")) // boolean operations; x & y
                        {
                            PushOperatorExpression(ShaderOperatorType.And, evaluationStack);
                        }
                        else if (name.StartsWith("or")) // x | y
                        {
                            PushOperatorExpression(ShaderOperatorType.Or, evaluationStack);
                        }
                        else if (name.StartsWith("shl")) // bit twiddling; x << y
                        {
                            PushOperatorExpression(ShaderOperatorType.ShiftLeft, evaluationStack);
                        }
                        else if (name.StartsWith("shr")) // x >> y
                        {
                            PushOperatorExpression(ShaderOperatorType.ShiftRight, evaluationStack);
                        }
                        else if (name.StartsWith("ceq")) // comparisons; equal
                        {
                            PushOperatorExpression(ShaderOperatorType.Equal, evaluationStack);
                        }
                        else if (name.StartsWith("cgt")) // greater
                        {
                            PushOperatorExpression(ShaderOperatorType.Greater, evaluationStack);
                        }
                        else if (name.StartsWith("clt")) // less
                        {
                            PushOperatorExpression(ShaderOperatorType.Less, evaluationStack);
                        }
                        else if (name.StartsWith("conv")) // cast one type to the other
                        {
                            int typeIndex = name.IndexOfAny(new char[] { 'u', 'i', 'r' });
                            if (typeIndex < 0)
                            {
                                throw new ArgumentException("Invalid convert instruction!");
                            }

                            // type is either 1 or 2 characters
                            // not clear at all, had to delve into opcodes
                            int end = typeIndex + 1;
                            if (name.Length > end && name[end] != '_' && name[end] != '.')
                            {
                                end++;
                            }

                            string conversionType = name[typeIndex..end];
                            string value = evaluationStack.Pop();
                            evaluationStack.Push($"{sConversionTypes[conversionType]}({value})");
                        }
                        else
                        {
                            // explicit cases
                            switch (name)
                            {
                                case "not": // boolean inversion
                                    PushOperatorExpression(ShaderOperatorType.Not, evaluationStack);
                                    break;
                                case "pop": // pop the top value from the stack
                                    {
                                        var expression = evaluationStack.Pop();
                                        builder.AppendLine($"{expression};");
                                    }

                                    break;
                                case "initobj": // initialize a value-typed object
                                    evaluationStack.Pop();
                                    break;
                                case "newobj": // initialize a new class-typed object
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
                                        var expressionString = CreateParameterExpressionString(evaluationStack, type, constructorParameters);

                                        evaluationStack.Push($"{typeName}({expressionString})");
                                    }

                                    break;
                                case "ret": // return the top value if such a thing exists
                                    if (evaluationStack.TryPop(out string? returnedExpression))
                                    {
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
                                    else
                                    {
                                        builder.AppendLine("return;");
                                    }

                                    break;
                                case "dup": // duplicate value on the stack
                                    evaluationStack.Push(evaluationStack.Peek());
                                    break;
                                default:
                                    if (name.StartsWith('b')) // comparison-jump
                                    {
                                        bool validInstruction = true;
                                        var operatorType = ShaderOperatorType.Equal;

                                        switch (name[1..3])
                                        {
                                            case "eq":
                                                operatorType = ShaderOperatorType.Equal;
                                                break;
                                            case "ge":
                                                operatorType = ShaderOperatorType.GreaterEqual;
                                                break;
                                            case "gt":
                                                operatorType = ShaderOperatorType.Greater;
                                                break;
                                            case "le":
                                                operatorType = ShaderOperatorType.LessEqual;
                                                break;
                                            case "lt":
                                                operatorType = ShaderOperatorType.Less;
                                                break;
                                            case "ne":
                                                operatorType = ShaderOperatorType.NotEqual;
                                                break;
                                            default:
                                                validInstruction = false;
                                                break;
                                        }

                                        if (validInstruction)
                                        {
                                            PushOperatorExpression(operatorType, evaluationStack);
                                            jumps.Add(new JumpInstruction
                                            {
                                                Offset = instruction.Offset,
                                                Destination = Convert.ToInt32(instruction.Operand!),
                                                Condition = new JumpCondition
                                                {
                                                    Type = ConditionalType.True,
                                                    Expression = evaluationStack.Pop()
                                                }
                                            });

                                            break;
                                        }
                                    }

                                    throw new InvalidOperationException($"Instruction {name} has not been implemented yet!");
                            }
                        }
                    }
                }

                // note(nora): ewwww
                using (Profiler.Event("Parse shader IL jumps"))
                {
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

                        int instructionIndex = mapCollection.OffsetInstructionMap[jump.Destination];
                        int jumpInsertDestinationOffset = mapCollection.InstructionOffsets[instructionIndex + 1];
                        ILInstruction? whileJumpInstruction = instructionIndex > 1 ? instructions[instructionIndex - 1] : null;

                        instructionIndex = mapCollection.OffsetInstructionMap[jump.Offset];
                        int jumpInsertOffset = mapCollection.InstructionOffsets[instructionIndex + 1];

                        if (condition.Type != ConditionalType.Unconditional)
                        {
                            var expression = condition.Type != ConditionalType.True ? $"int({condition.Expression}) == 0" : condition.Expression;
                            if (whileJumpInstruction?.OpCode.Name?.ToLower()?.StartsWith("br") ?? false)
                            {
                                var match = Regex.Match(condition.Expression!, @"^var_\d+$");
                                if (match.Success)
                                {
                                    var variableAssignmentInstructionOffset = mapCollection.InstructionOffsets[instructionIndex - 2];
                                    var variableAssignmentSourceOffset = mapCollection.SourceOffsets[variableAssignmentInstructionOffset];
                                    var jumpSourceOffset = mapCollection.SourceOffsets[jumpInsertOffset];

                                    var source = builder.ToString();
                                    var variableAssignment = source[variableAssignmentSourceOffset..jumpSourceOffset];

                                    InsertCode(jumpInsertDestinationOffset, variableAssignment, builder, mapCollection);
                                }

                                startCode = $"while ({expression}) {{\n";
                                endCode = "}\n";
                            }
                            else
                            {
                                startCode = "do {\n";
                                endCode = $"}} while ({expression});\n";
                            }
                        }
                        else
                        {
                            startCode = "while (true) {\n";
                            endCode = "}\n";
                        }

                        InsertCode(jumpInsertDestinationOffset, startCode, builder, mapCollection);
                        InsertCode(jumpInsertOffset, endCode, builder, mapCollection);
                    }

                    foreach (var jump in nonLoopJumps)
                    {
                        var condition = jump.Condition;
                        var containingScope = FindContainingScope(jump.Offset);
                        if (containingScope is not null && jump.Destination > containingScope.EndOffset)
                        {
                            var currentLoop = containingScope;
                            while (currentLoop is not null && currentLoop.Type != ScopeType.Loop)
                            {
                                currentLoop = currentLoop.Parent;
                            }

                            if (currentLoop?.Type == ScopeType.Loop && jump.Destination > currentLoop.EndOffset)
                            {
                                var currentParent = currentLoop.Parent;
                                if (currentParent is not null && jump.Destination > currentParent.EndOffset)
                                {
                                    throw new InvalidOperationException("Invalid break statement!");
                                }

                                string code = "break;\n";
                                if (condition.Type != ConditionalType.Unconditional)
                                {
                                    var expression = condition.Type != ConditionalType.False ? condition.Expression : $"int({condition.Expression}) == 0";
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
                            continue;
                        }

                        var ifScope = new Scope(jump.Offset, jump.Destination - jump.Offset, ScopeType.Conditional);
                        if (!AddScope(ifScope))
                        {
                            throw new InvalidOperationException("Invalid scope generated for if statement!");
                        }

                        var ifExpression = condition.Type != ConditionalType.True ? condition.Expression : $"int({condition.Expression}) == 0";
                        var startCode = $"if ({ifExpression}) {{\n";

                        InsertCode(nextInstruction, startCode, builder, mapCollection);
                        InsertCode(jump.Destination, "}\n", builder, mapCollection);
                    }
                }
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
            using var addEvent = Profiler.Event();
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
            using var insertEvent = Profiler.Event();

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
            mStageResources.Clear();
            mSharedVariables.Clear();
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
            using var resolveEvent = Profiler.Event();

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
                IEnumerable<Type> evaluationList = definedStructs.Where(type => mStructDependencies[type].Define);
                while (evaluationList.Any())
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

                        if (!hasDependencies || structOrder.Contains(type))
                        {
                            if (!hasDependencies)
                            {
                                newEvaluationList.Add(type);
                            }

                            continue;
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
            using var resolveEvent = Profiler.Event();

            var dependencyInfo = new List<EvaluationMethodInfo>();
            var dependencyInfoIndices = new Dictionary<MethodInfo, int>();

            foreach (var method in mDependencyGraph.Keys)
            {
                var dependencies = mDependencyGraph[method].Dependencies;
                var definedDependencies = dependencies.Where(dependency => dependency.GetCustomAttribute<BuiltinShaderFunctionAttribute>() is null).ToList();

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
                        Dependencies = definedDependencies,
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

            var evaluationList = dependencyInfo.Select(info => info.Method);
            var functionOrder = new List<MethodInfo>();

            while (evaluationList.Any())
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

                    if (!hasDependencies || functionOrder.Contains(method))
                    {
                        if (!hasDependencies)
                        {
                            newEvaluationList.Add(method);
                        }

                        continue;
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

        private static string GetFieldDefinition(string fieldName, StructFieldInfo fieldInfo)
        {
            string definition = $"{fieldInfo.TypeName} {fieldName}";
            if (fieldInfo.ArraySize != 0)
            {
                definition += '[';
                if (fieldInfo.ArraySize > 0)
                {
                    definition += fieldInfo.ArraySize;
                }

                definition += ']';
            }

            return $"{definition};";
        }

        protected override StageOutput TranspileStage(Type type, MethodInfo entrypoint, ShaderStage stage)
        {
            using var transpileEvent = Profiler.Event();
            ProcessMethod(type, entrypoint, true);

            var builder = new StringBuilder();
            builder.AppendLine("#version 450\n");

            var structOrder = ResolveStructOrder();
            foreach (var definedStruct in structOrder)
            {
                var info = mStructDependencies[definedStruct];
                if (!info.Define)
                {
                    continue;
                }

                var name = GetTypeName(definedStruct, type, true);
                builder.AppendLine($"struct {name} {{");

                foreach (var fieldName in info.DefinedFields.Keys)
                {
                    var fieldInfo = info.DefinedFields[fieldName];
                    var definition = GetFieldDefinition(fieldName, fieldInfo);
                    builder.AppendLine(definition);
                }

                builder.AppendLine("};");
            }

            foreach (string fieldName in mStageIO.Keys)
            {
                var fieldData = mStageIO[fieldName];

                var direction = fieldData.Direction.ToString().ToLower();
                if (fieldData.Flat)
                {
                    direction += " flat";
                }

                builder.AppendLine($"layout(location = {fieldData.Location}) {direction} {fieldData.TypeName} {fieldName};");
            }

            switch (stage)
            {
                case ShaderStage.Compute:
                    {
                        var attribute = entrypoint.GetCustomAttribute<NumThreadsAttribute>();
                        if (attribute is not null)
                        {
                            builder.AppendLine($"layout(local_size_x = {attribute.X}, local_size_y = {attribute.Y}, local_size_z = {attribute.Z}) in;");
                        }
                    }
                    break;
                case ShaderStage.Geometry:
                    {
                        var attribute = entrypoint.GetCustomAttribute<GeometryPrimitivesAttribute>();
                        if (attribute is not null)
                        {
                            var input = attribute.Input switch
                            {
                                GeometryInputPrimitive.Points => "points",
                                GeometryInputPrimitive.Lines => "lines",
                                GeometryInputPrimitive.LinesAdjacency => "lines_adjacency",
                                GeometryInputPrimitive.Triangles => "triangles",
                                GeometryInputPrimitive.TrianglesAdjacency => "triangles_adjacency",
                                _ => throw new ArgumentException("Invalid input primitive!")
                            };

                            var output = attribute.Output switch
                            {
                                GeometryOutputPrimitive.Points => "points",
                                GeometryOutputPrimitive.LineStrip => "line_strip",
                                GeometryOutputPrimitive.TriangleStrip => "triangle_strip",
                                _ => throw new ArgumentException("Invalid output primitive!")
                            };

                            builder.AppendLine($"layout({input}) in;");
                            builder.AppendLine($"layout({output}, max_vertices = {attribute.MaxVertices}) out;");
                        }
                    }
                    break;
            }

            foreach (string fieldName in mStageResources.Keys)
            {
                var resourceData = mStageResources[fieldName];
                var resourceType = resourceData.Type;
                var dataType = resourceData.ResourceType;

                var resourceTypeField = typeof(ShaderResourceType).GetField(resourceType.ToString());
                var definedName = GetMemberName(resourceTypeField!);

                var resourceTypeName = (definedName ?? resourceType.ToString()).ToLower();
                builder.Append($"layout({resourceData.Layout}) {resourceTypeName} ");

                if (dataType.IsValueType)
                {
                    builder.AppendLine($"{fieldName}_struct_definition_ {{");

                    var info = mStructDependencies[dataType];
                    foreach (var currentFieldName in info.DefinedFields.Keys)
                    {
                        var fieldInfo = info.DefinedFields[currentFieldName];
                        var definition = GetFieldDefinition(currentFieldName, fieldInfo);
                        builder.AppendLine(definition);
                    }

                    builder.Append("} ");
                }
                else
                {
                    var typeName = GetTypeName(dataType, type, true);
                    builder.Append(typeName + ' ');
                }

                string resourceNameDeclaration = fieldName;
                if (resourceData.ArraySize >= 0)
                {
                    resourceNameDeclaration += $"[{resourceData.ArraySize}]";
                }

                builder.AppendLine(resourceNameDeclaration + ';');
            }

            foreach (string fieldName in mSharedVariables.Keys)
            {
                var fieldType = mSharedVariables[fieldName];
                var typeName = GetTypeName(fieldType, type, true);

                builder.AppendLine($"shared {typeName} {fieldName};");
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
        private readonly Dictionary<string, ShaderResource> mStageResources;
        private readonly Dictionary<string, Type> mSharedVariables;
        private readonly Dictionary<MethodInfo, TranslatedMethodInfo> mDependencyGraph;
        private readonly Dictionary<Type, StructDependencyInfo> mStructDependencies;
        private readonly List<Scope> mMethodScopes;
    }
}