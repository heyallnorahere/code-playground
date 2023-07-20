using Optick.NET;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace CodePlayground
{
    internal delegate object? ResolveOperandCallback(byte[] il, Module module);
    internal sealed class ResolveOperandCallbackAttribute : Attribute
    {
        public ResolveOperandCallbackAttribute(OperandType type)
        {
            Type = type;
        }

        public OperandType Type { get; }
    }

    public struct ILInstruction
    {
        public OpCode OpCode { get; set; }
        public object? Operand { get; set; }
        public int Offset { get; set; }
        public byte[] InstructionData { get; set; }
    }

    public sealed class ILParser
    {
        internal const byte MultibyteOpCodePrefix = 0xfe;

        private static readonly Dictionary<byte, OpCode> sSingleByteInstructions, sMultiByteInstructions;
        static ILParser()
        {
            sSingleByteInstructions = new Dictionary<byte, OpCode>();
            sMultiByteInstructions = new Dictionary<byte, OpCode>();

            var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(OpCode))
                {
                    continue;
                }

                var code = (OpCode)field.GetValue(null)!;
                var codeValue = (ushort)code.Value;

                if (codeValue < 0x100)
                {
                    sSingleByteInstructions.Add((byte)code.Value, code);
                }
                else if ((codeValue >> 8) != MultibyteOpCodePrefix)
                {
                    throw new InvalidOperationException($"Invalid OpCode: {code}");
                }
                else
                {
                    sMultiByteInstructions.Add((byte)(codeValue & 0xff), code);
                }
            }
        }

        public ILParser()
        {
            Instructions = new List<ILInstruction>();

            mPosition = 0;
            mResolveCallbacks = LoadResolveOperandCallbacks();
        }

        private IReadOnlyDictionary<OperandType, ResolveOperandCallback> LoadResolveOperandCallbacks()
        {
            using var loadEvent = OptickMacros.Event();

            var type = GetType();
            var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);

            var result = new Dictionary<OperandType, ResolveOperandCallback>();
            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<ResolveOperandCallbackAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                if (result.ContainsKey(attribute.Type))
                {
                    throw new InvalidOperationException($"Duplicate resolve callback for operand type: {attribute.Type}");
                }

                var callback = Delegate.CreateDelegate(typeof(ResolveOperandCallback), this, method);
                result.Add(attribute.Type, (ResolveOperandCallback)callback);
            }

            return result;
        }

        public void Parse(byte[] il, Module module)
        {
            using var parseEvent = OptickMacros.Event();

            mPosition = 0;
            while (mPosition < il.Length)
            {
                int offset = mPosition;

                OpCode code;
                byte codeValue = il[mPosition++];
                if (codeValue != MultibyteOpCodePrefix)
                {
                    code = sSingleByteInstructions[codeValue];
                }
                else
                {
                    codeValue = il[mPosition++];
                    code = sMultiByteInstructions[codeValue];
                }

                var operand = mResolveCallbacks[code.OperandType].Invoke(il, module);
                Instructions.Add(new ILInstruction
                {
                    OpCode = code,
                    Offset = offset,
                    Operand = operand,
                    InstructionData = il[offset..mPosition]
                });
            }
        }

#region Resolve functions
        private unsafe T ResolveUnmanaged<T>(byte[] il) where T : unmanaged
        {
            using var resolveEvent = OptickMacros.Event();
            fixed (byte* data = il)
            {
                nint address = (nint)data + mPosition;
                T result = Marshal.PtrToStructure<T>(address);

                mPosition += sizeof(T);
                return result;
            }
        }

        [ResolveOperandCallback(OperandType.InlineI)]
        private object? ResolveInt32(byte[] il, Module module)
        {
            return ResolveUnmanaged<int>(il);
        }

        [ResolveOperandCallback(OperandType.InlineI8)]
        private object? ResolveInt64(byte[] il, Module module)
        {
            return ResolveUnmanaged<long>(il);
        }

        [ResolveOperandCallback(OperandType.InlineR)]
        private object? ResolveDouble(byte[] il, Module module)
        {
            return ResolveUnmanaged<double>(il);
        }

        [ResolveOperandCallback(OperandType.InlineString)]
        private object? ResolveString(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveString(token);
        }

        [ResolveOperandCallback(OperandType.InlineNone)]
        private object? ResolveNull(byte[] il, Module module)
        {
            return null;
        }

        [ResolveOperandCallback(OperandType.InlineBrTarget)]
        private object? ResolveBranchTarget(byte[] il, Module module)
        {
            int offset = ResolveUnmanaged<int>(il);
            return mPosition + offset;
        }

        [ResolveOperandCallback(OperandType.InlineField)]
        private object? ResolveField(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveField(token);
        }

        [ResolveOperandCallback(OperandType.InlineMethod)]
        private object? ResolveMethod(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveMethod(token);
        }

        [ResolveOperandCallback(OperandType.InlineSig)]
        private object? ResolveSignature(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveSignature(token);
        }

        [ResolveOperandCallback(OperandType.InlineSwitch)]
        private object? ResolveSwitch(byte[] il, Module module)
        {
            using var resolveEvent = OptickMacros.Event();
            int count = ResolveUnmanaged<int>(il);

            var caseAddresses = new int[count];
            for (int i = 0; i < count; i++)
            {
                caseAddresses[i] = ResolveUnmanaged<int>(il);
            }

            var cases = new int[count];
            for (int i = 0; i < count; i++)
            {
                cases[i] = caseAddresses[i] + mPosition;
            }

            return cases;
        }

        [ResolveOperandCallback(OperandType.InlineTok)]
        private object? ResolveToken(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveType(token); // are we sure about this?
        }

        [ResolveOperandCallback(OperandType.InlineType)]
        private object? ResolveType(byte[] il, Module module)
        {
            int token = ResolveUnmanaged<int>(il);
            return module.ResolveType(token);
        }

        [ResolveOperandCallback(OperandType.InlineVar)]
        private object? ResolveVariable(byte[] il, Module module)
        {
            return ResolveUnmanaged<ushort>(il);
        }

        [ResolveOperandCallback(OperandType.ShortInlineBrTarget)]
        private object? ResolveShortBranchTarget(byte[] il, Module module)
        {
            var offset = ResolveUnmanaged<sbyte>(il);
            return mPosition + offset;
        }

        [ResolveOperandCallback(OperandType.ShortInlineI)]
        private object? ResolveByte(byte[] il, Module module)
        {
            return ResolveUnmanaged<byte>(il);
        }

        [ResolveOperandCallback(OperandType.ShortInlineR)]
        private object? ResolveFloat(byte[] il, Module module)
        {
            return ResolveUnmanaged<float>(il);
        }

        [ResolveOperandCallback(OperandType.ShortInlineVar)]
        private object? ResolveShortVariable(byte[] il, Module module)
        {
            return ResolveUnmanaged<byte>(il);
        }
#endregion

        public List<ILInstruction> Instructions { get; }

        private int mPosition;
        private readonly IReadOnlyDictionary<OperandType, ResolveOperandCallback> mResolveCallbacks;
    }
}