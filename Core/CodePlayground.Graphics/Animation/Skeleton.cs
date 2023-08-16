using Optick.NET;
using System.Collections.Generic;
using System.Numerics;

namespace CodePlayground.Graphics.Animation
{
    internal struct Bone
    {
        public int Parent { get; set; }
        public string Name { get; set; }
        public Matrix4x4 Transform { get; set; }
        public Matrix4x4 OffsetMatrix { get; set; }
        public Matrix4x4 ParentTransform { get; set; }
        public HashSet<int> Children { get; set; }
    }

    public sealed class Skeleton
    {
        public Skeleton()
        {
            mBones = new List<Bone>();
        }

        public int AddBone(int parent, string name, Matrix4x4 transform, Matrix4x4 offsetMatrix, Matrix4x4 parentTransform)
        {
            using var addBoneEvent = OptickMacros.Event();
            int id = mBones.Count;

            mBones.Add(new Bone
            {
                Parent = parent,
                Name = name,
                Transform = transform,
                OffsetMatrix = offsetMatrix,
                ParentTransform = parentTransform,
                Children = new HashSet<int>()
            });

            if (parent >= 0)
            {
                mBones[parent].Children.Add(id);
            }

            return id;
        }

        public int FindBoneByName(string name) => mBones.FindIndex(bone => bone.Name == name);
        public int GetParent(int id) => mBones[id].Parent;
        public string GetName(int id) => mBones[id].Name;
        public Matrix4x4 GetTransform(int id) => mBones[id].Transform;
        public Matrix4x4 GetOffsetMatrix(int id) => mBones[id].OffsetMatrix;
        public Matrix4x4 GetParentTransform(int id) => mBones[id].ParentTransform;
        public IReadOnlySet<int> GetChildren(int id) => mBones[id].Children;

        public int BoneCount => mBones.Count;

        private readonly List<Bone> mBones;
    }
}