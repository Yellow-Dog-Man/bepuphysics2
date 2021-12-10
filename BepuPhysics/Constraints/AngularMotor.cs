﻿using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;

namespace BepuPhysics.Constraints
{
    /// <summary>
    /// Constrains the relative angular velocity between two bodies to a target.
    /// </summary>
    public struct AngularMotor : ITwoBodyConstraintDescription<AngularMotor>
    {
        /// <summary>
        /// Target relative angular velocity between A and B, stored in A's local space. Target world space angular velocity of B is AngularVelocityA + TargetVelocityLocalA * OrientationA.
        /// </summary>
        public Vector3 TargetVelocityLocalA;
        /// <summary>
        /// Motor control parameters.
        /// </summary>
        public MotorSettings Settings;

        public readonly int ConstraintTypeId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return AngularMotorTypeProcessor.BatchTypeId;
            }
        }

        public readonly Type TypeProcessorType => typeof(AngularMotorTypeProcessor);

        public readonly void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
        {
            ConstraintChecker.AssertValid(Settings, nameof(AngularMotor));
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var target = ref GetOffsetInstance(ref Buffer<AngularMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.WriteFirst(TargetVelocityLocalA, ref target.TargetVelocityLocalA);
            MotorSettingsWide.WriteFirst(Settings, ref target.Settings);
        }

        public readonly void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out AngularMotor description)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var source = ref GetOffsetInstance(ref Buffer<AngularMotorPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.ReadFirst(source.TargetVelocityLocalA, out description.TargetVelocityLocalA);
            MotorSettingsWide.ReadFirst(source.Settings, out description.Settings);
        }
    }

    public struct AngularMotorPrestepData
    {
        public Vector3Wide TargetVelocityLocalA;
        public MotorSettingsWide Settings;
    }

    public struct AngularMotorProjection
    {
        public Symmetric3x3Wide EffectiveMass;
        public Vector3Wide BiasImpulse;
        public Vector<float> SoftnessImpulseScale;
        public Vector<float> MaximumImpulse;
        public Symmetric3x3Wide ImpulseToVelocityA;
        public Symmetric3x3Wide NegatedImpulseToVelocityB;
    }


    public struct AngularMotorFunctions : ITwoBodyConstraintFunctions<AngularMotorPrestepData, AngularMotorProjection, Vector3Wide>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prestep(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
            float dt, float inverseDt, ref AngularMotorPrestepData prestep, out AngularMotorProjection projection)
        {
            projection.ImpulseToVelocityA = inertiaA.InverseInertiaTensor;
            projection.NegatedImpulseToVelocityB = inertiaB.InverseInertiaTensor;

            //Jacobians are just the identity matrix.
            MotorSettingsWide.ComputeSoftness(prestep.Settings, dt, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale, out projection.MaximumImpulse);

            Symmetric3x3Wide.Add(projection.ImpulseToVelocityA, projection.NegatedImpulseToVelocityB, out var unsoftenedInverseEffectiveMass);
            Symmetric3x3Wide.Invert(unsoftenedInverseEffectiveMass, out var unsoftenedEffectiveMass);
            Symmetric3x3Wide.Scale(unsoftenedEffectiveMass, effectiveMassCFMScale, out projection.EffectiveMass);

            QuaternionWide.TransformWithoutOverlap(prestep.TargetVelocityLocalA, orientationA, out var biasVelocity);
            Symmetric3x3Wide.TransformWithoutOverlap(biasVelocity, projection.EffectiveMass, out projection.BiasImpulse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref AngularMotorProjection projection, ref Vector3Wide accumulatedImpulse)
        {
            AngularServoFunctions.ApplyImpulse(ref velocityA.Angular, ref velocityB.Angular, projection.ImpulseToVelocityA, projection.NegatedImpulseToVelocityB, accumulatedImpulse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref AngularMotorProjection projection, ref Vector3Wide accumulatedImpulse)
        {
            AngularServoFunctions.Solve(ref velocityA, ref velocityB, projection.EffectiveMass, projection.SoftnessImpulseScale, projection.BiasImpulse,
                projection.MaximumImpulse, projection.ImpulseToVelocityA, projection.NegatedImpulseToVelocityB, ref accumulatedImpulse);
        }

        public void WarmStart2(in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide positionB, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, ref AngularMotorPrestepData prestep, ref Vector3Wide accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {
            AngularServoFunctions.ApplyImpulse(ref wsvA.Angular, ref wsvB.Angular, inertiaA.InverseInertiaTensor, inertiaB.InverseInertiaTensor, accumulatedImpulses);
        }

        public void Solve2(in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide positionB, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, float dt, float inverseDt, ref AngularMotorPrestepData prestep, ref Vector3Wide accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {
            //Jacobians are just the identity matrix.
            MotorSettingsWide.ComputeSoftness(prestep.Settings, dt, out var effectiveMassCFMScale, out var softnessImpulseScale, out var maximumImpulse);

            Symmetric3x3Wide.Add(inertiaB.InverseInertiaTensor, inertiaB.InverseInertiaTensor, out var unsoftenedInverseEffectiveMass);
            Symmetric3x3Wide.Invert(unsoftenedInverseEffectiveMass, out var unsoftenedEffectiveMass);
            //Note that we don't scale the effective mass directly; instead scale CSI.

            QuaternionWide.TransformWithoutOverlap(prestep.TargetVelocityLocalA, orientationA, out var biasVelocity);

            //csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - (csiaLinear + csiaAngular + csibLinear + csibAngular);
            Vector3Wide.Subtract(wsvA.Angular, wsvB.Angular, out var csv);
            Vector3Wide.Subtract(biasVelocity, csv, out csv);
            Symmetric3x3Wide.TransformWithoutOverlap(csv, unsoftenedEffectiveMass, out var csi);
            csi *= effectiveMassCFMScale;
            Vector3Wide.Scale(accumulatedImpulses, softnessImpulseScale, out var softnessComponent);
            Vector3Wide.Subtract(csi, softnessComponent, out csi);

            ServoSettingsWide.ClampImpulse(maximumImpulse, ref accumulatedImpulses, ref csi);
            AngularServoFunctions.ApplyImpulse(ref wsvA.Angular, ref wsvB.Angular, inertiaA.InverseInertiaTensor, inertiaB.InverseInertiaTensor, csi);
        }

        public bool RequiresIncrementalSubstepUpdates => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementallyUpdateForSubstep(in Vector<float> dt, in BodyVelocityWide wsvA, in BodyVelocityWide wsvB, ref AngularMotorPrestepData prestepData) { }
    }

    public class AngularMotorTypeProcessor : TwoBodyTypeProcessor<AngularMotorPrestepData, AngularMotorProjection, Vector3Wide, AngularMotorFunctions, AccessOnlyAngularWithoutPose, AccessOnlyAngularWithoutPose, AccessOnlyAngular, AccessOnlyAngularWithoutPose>
    {
        public const int BatchTypeId = 30;
    }
}

