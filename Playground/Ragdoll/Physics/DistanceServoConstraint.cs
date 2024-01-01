using BepuPhysics;
using BepuPhysics.Constraints;
using CodePlayground;
using ImGuiNET;
using System.Numerics;

namespace Ragdoll.Physics
{
    [RegisteredConstraint(ConstraintType.DistanceServo)]
    public sealed class DistanceServoConstraint : Constraint
    {
        public DistanceServoConstraint()
        {
            mConstraintExists = false;
            mConstraint = new DistanceServo
            {
                LocalOffsetA = Vector3.Zero,
                LocalOffsetB = Vector3.Zero,
                TargetDistance = 25f,
                ServoSettings = ServoSettings.Default,
                SpringSettings = new SpringSettings
                {
                    Frequency = 30f,
                    DampingRatio = 1f
                }
            };
        }

        public override bool IsInitialized => mConstraintExists;

        public override void Create(Simulation simulation, BodyHandle bodyA, BodyHandle bodyB)
        {
            using var createEvent = Profiler.Event();
            if (mConstraintExists)
            {
                Destroy();
            }

            mSimulation = simulation;
            mHandle = mSimulation.Solver.Add(bodyA, bodyB, mConstraint);
            mConstraintExists = true;
        }

        public override void Destroy()
        {
            if (!mConstraintExists)
            {
                return;
            }

            using var destroyEvent = Profiler.Event();
            mSimulation!.Solver.Remove(mHandle);
            mConstraintExists = false;
        }

        public override void Edit()
        {
            using var editEvent = Profiler.Event();
            bool changed = false;

            changed |= ImGui.DragFloat3("Local offset A", ref mConstraint.LocalOffsetA, 0.05f);
            changed |= ImGui.DragFloat3("Local offset B", ref mConstraint.LocalOffsetB, 0.05f);
            changed |= ImGui.DragFloat("Target distance", ref mConstraint.TargetDistance, 1f, 0f);
            changed |= ConstraintEditUtilities.EditServoSettings(ref mConstraint.ServoSettings);
            changed |= ConstraintEditUtilities.EditSpringSettings(ref mConstraint.SpringSettings);

            if (changed && mConstraintExists)
            {
                Update();
            }
        }

        public override void Update()
        {
            using var updateEvent = Profiler.Event();
            if (!mConstraintExists)
            {
                return;
            }

            mSimulation!.Solver.ApplyDescription(mHandle, mConstraint);
        }

        private ConstraintHandle mHandle;
        private DistanceServo mConstraint;
        private bool mConstraintExists;

        private Simulation? mSimulation;
    }
}