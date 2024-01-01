using System;
using System.Numerics;

namespace CodePlayground.Graphics.Animation
{
    public sealed class SkeletonController
    {
        public SkeletonController(Skeleton skeleton)
        {
            mSkeleton = skeleton;

            mBoneTransforms = new Matrix4x4[skeleton.BoneCount];
            Array.Fill(mBoneTransforms, Matrix4x4.Identity);
        }

        public Skeleton Skeleton => mSkeleton;
        public int BoneCount => mSkeleton.BoneCount;

        public Matrix4x4 this[int index]
        {
            get => mBoneTransforms[index];
            set => mBoneTransforms[index] = value;
        }

        public void Update(Action<Matrix4x4[]> callback)
        {
            using var updateEvent = Profiler.Event();

            int boneCount = mSkeleton.BoneCount;
            var results = new Matrix4x4[boneCount];
            var globalTransforms = new Matrix4x4[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                int parent = mSkeleton.GetParent(i);
                var parentTransform = parent < 0 ? mSkeleton.GetParentTransform(i) : globalTransforms[parent];
                var transformMatrix = mSkeleton.GetTransform(i);
                var offsetMatrix = mSkeleton.GetOffsetMatrix(i);

                var nodeTransform = transformMatrix * mBoneTransforms[i];
                var globalTransform = globalTransforms[i] = parentTransform * nodeTransform;
                results[i] = globalTransform * offsetMatrix;
            }

            callback.Invoke(results);
        }

        private readonly Skeleton mSkeleton;
        private readonly Matrix4x4[] mBoneTransforms;
    }
}