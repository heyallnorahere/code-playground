using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
        public StageIODirection Direction { get; set; }
        public int Location { get; set; }
        public string TypeName { get; set; }
    }

    internal struct ShaderResource
    {
        public string Layout { get; set; }
        public string TypeName { get; set; }
        public ShaderResourceType Type { get; set; }
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
}
