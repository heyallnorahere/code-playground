using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Layers;
using System.Numerics;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Bone controller")]
    public sealed class BoneControllerComponent
    {
        public BoneControllerComponent()
        {
            mSkeleton = mReference = Scene.Null;
        }

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            var dispatcher = new ComponentEventDispatcher(eventInfo);

            dispatcher.Dispatch(ComponentEventID.Added, OnComponentAdded);
            dispatcher.Dispatch(ComponentEventID.Edited, OnEdit);
            dispatcher.Dispatch(ComponentEventID.PreBoneUpdate, PreBoneUpdate);

            return dispatcher;
        }

        private void OnComponentAdded(Scene scene, ulong entity)
        {
            mScene = scene;
            mEntity = entity;
        }

        private void OnEdit()
        {
            if (mScene is null)
            {
                return;
            }

            ImGuiUtilities.DragDropEntityTarget("Reference", mScene, ref mReference);
            ImGuiUtilities.DragDropEntityTarget("Animated skeleton", mScene, ref mSkeleton);

            if (mSkeleton != Scene.Null && mScene.TryGetComponent(mSkeleton, out RenderedModelComponent? renderedModel) && renderedModel.BoneController is not null)
            {
                var boneController = renderedModel.BoneController;
                var skeleton = boneController.Skeleton;

                if (ImGui.BeginCombo("Animated bone", mBone < 0 ? "--No bone--" : skeleton.GetName(mBone)))
                {
                    for (int i = 0; i < skeleton.BoneCount; i++)
                    {
                        bool isSelected = mBone == i;
                        if (ImGui.Selectable(skeleton.GetName(i), isSelected))
                        {
                            if (mBone >= 0)
                            {
                                boneController[mBone] = Matrix4x4.Identity;
                            }

                            AnimatedBone = i; // does all the "on changed" matrix calculations
                        }

                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else
            {
                mBone = -1;
            }
        }

        private void PreBoneUpdate()
        {
            if (mSkeleton == Scene.Null || mReference == Scene.Null || mScene is null || mBone < 0)
            {
                return;
            }

            if (!mScene.TryGetComponent(mSkeleton, out RenderedModelComponent? renderedModel) || renderedModel.BoneController is null)
            {
                return;
            }

            if (!mScene.TryGetComponent(mReference, out TransformComponent? referenceTransform))
            {
                return;
            }

            var referenceMatrix = referenceTransform.CreateMatrix(TransformComponents.NonDeformative);
            if (!Matrix4x4.Invert(referenceMatrix, out Matrix4x4 inverseReference))
            {
                return;
            }

            if (!mScene.TryGetComponent(mEntity, out TransformComponent? controllerTransform))
            {
                return;
            }

            Matrix4x4 transformMatrix = controllerTransform.CreateMatrix(TransformComponents.NonDeformative);
            var boneController = renderedModel.BoneController;

            boneController[mBone] = mInverseLocalBoneTransform * inverseReference * transformMatrix;
        }

        private Scene? mScene;
        private ulong mEntity, mSkeleton, mReference;
        private int mBone;
        private Matrix4x4 mInverseLocalBoneTransform;

        public ulong AnimatedSkeleton
        {
            get => mSkeleton;
            set => mSkeleton = value;
        }

        public ulong ReferenceEntity
        {
            get => mReference;
            set => mReference = value;
        }

        public int AnimatedBone
        {
            get => mBone;
            set
            {
                var renderedModel = mScene!.GetComponent<RenderedModelComponent>(mSkeleton);
                var boneController = renderedModel.BoneController!;
                var skeleton = boneController.Skeleton;

                mBone = value;
                if (mBone >= 0)
                {
                    var localTransform = skeleton.GetTransform(mBone);
                    if (MatrixMath.Decompose(localTransform, out Vector3 translation, out _, out _))
                    {
                        mInverseLocalBoneTransform = Matrix4x4.CreateTranslation(-translation);
                    }
                    else
                    {
                        mInverseLocalBoneTransform = Matrix4x4.Identity;
                    }
                }
            }
        }
    }
}