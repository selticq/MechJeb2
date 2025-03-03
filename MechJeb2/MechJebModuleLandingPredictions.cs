﻿extern alias JetBrainsAnnotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Smooth.Dispose;
using UnityEngine;
using UnityToolbag;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace MuMech
{
    public class MechJebModuleLandingPredictions : ComputerModule
    {
        // TODO Move the endASL code to the CheckResult method
        // For Compatibility with RPM
        public ReentrySimulation.Result GetResult() => Result;

        public ReentrySimulation.Result Result =>
            //if (result != null)
            //{
            //    if (result.body != null)
            //    {
            //        simDragScalar = result.prediction.firstDrag;
            //        simLiftScalar = result.prediction.firstLift;
            //        simDynamicPressurePa = result.prediction.dynamicPressurekPa * 1000;
            //        simMach = result.prediction.mach;
            //        simSpeedOfSound = result.prediction.speedOfSound;
            //
            //        if (result.debugLog != "")
            //        {
            //
            //            MechJebCore.print("Now".PadLeft(8)
            //                       + " Alt:" + vesselState.altitudeASL.ToString("F0").PadLeft(6)
            //                       + " Vel:" + vesselState.speedOrbital.ToString("F2").PadLeft(8)
            //                       + " AirVel:" + vesselState.speedSurface.ToString("F2").PadLeft(8)
            //                       + " SoS:" +  vesselState.speedOfSound.ToString("F2").PadLeft(6)
            //                       + " mach:" + vesselState.mach.ToString("F2").PadLeft(6)
            //                       + " dynP:" + (vesselState.dynamicPressure / 1000).ToString("F5").PadLeft(9)
            //                       + " Temp:" + vessel.atmosphericTemperature.ToString("F2").PadLeft(8)
            //                       + " Lat:" + vesselState.latitude.ToString("F2").PadLeft(6));
            //
            //
            //            MechJebCore.print(result.debugLog);
            //            result.debugLog = "";
            //
            //            Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(vessel.transform.position);
            //            Vector3 sunVector = (FlightGlobals.Bodies[0].scaledBody.transform.position - scaledPos).normalized;
            //
            //            float sunDot = Vector3.Dot(sunVector, vessel.upAxis);
            //            float sunAxialDot = Vector3.Dot(sunVector, vessel.mainBody.bodyTransform.up);
            //            MechJebCore.print("sunDot " + sunDot.ToString("F3") + " sunAxialDot " + sunAxialDot.ToString("F3") + " " + PhysicsGlobals.DragUsesAcceleration);
            //
            //        }
            //    }
            //}
            result;

        public ReentrySimulation.Result GetErrorResult()
        {
            if (null != errorResult)
            {
                if (null != errorResult.Body)
                {
                    errorResult.EndASL = errorResult.Body.TerrainAltitude(errorResult.EndPosition.Latitude, errorResult.EndPosition.Longitude);
                }
            }

            return errorResult;
        }

        [ValueInfoItem("#MechJeb_SimDragScalar", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s²")] //Sim Drag Scalar
        public double simDragScalar;

        [ValueInfoItem("#MechJeb_SimLiftScalar", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s²")] //Sim Lift Scalar
        public double simLiftScalar;

        [ValueInfoItem("#MechJeb_SimDynaPressPa", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "Pa")] //Sim DynaPressPa
        public double simDynamicPressurePa;

        [ValueInfoItem("#MechJeb_SimsimMach", InfoItem.Category.Vessel, format = "F2")] //Sim simMach
        public double simMach;

        [ValueInfoItem("#MechJeb_SimSpdOfSnd", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s")] //Sim SpdOfSnd
        public double simSpeedOfSound;

        //inputs:
        [Persistent(pass = (int)Pass.GLOBAL)]
        public bool makeAerobrakeNodes = false;

        [Persistent(pass = (int)Pass.GLOBAL)]
        public bool showTrajectory = false;

        [Persistent(pass = (int)Pass.GLOBAL)]
        public bool worldTrajectory = true;

        [Persistent(pass = (int)Pass.GLOBAL)]
        public bool camTrajectory = false;

        public bool deployChutes     = false;
        public int  limitChutesStage = 0;

        //simulation inputs:
        public double
            decelEndAltitudeASL =
                0; // The altitude at which we need to have killed velocity - NOTE that this is not the same as the height of the predicted landing site.

        public IDescentSpeedPolicy descentSpeedPolicy            = null;  //simulate this descent speed policy
        public double              parachuteSemiDeployMultiplier = 3;     // this will get updated by the autopilot.
        public bool                runErrorSimulations           = false; // This will be set by the autopilot to turn error simulations on or off.

        //internal data:

        protected bool
            errorSimulationRunning; // the predictor can run two types of simulation - 1) simulations of the current situation. 2) simulations of the current situation with deliberate error introduced into the parachute multiplier to aid the statistical analysis of these results.

        protected readonly Stopwatch errorStopwatch = new Stopwatch();
        protected          long      millisecondsBetweenErrorSimulations;

        public bool SimulationRunning { get; private set; }

        protected readonly Stopwatch stopwatch = new Stopwatch();
        public             double    SimulationRunningTime => SimulationRunning ? stopwatch.ElapsedMilliseconds / 1000d : 0;
        protected          long      millisecondsBetweenSimulations;

        protected ReentrySimulation.Result result;
        protected ReentrySimulation.Result errorResult;

        public ManeuverNode aerobrakeNode;

        protected const int interationsPerSecond = 5; // the number of times that we want to try to run the simulation each second.

        protected double
            dt = 0.2; // the suggested dt for each timestep in the simulations. This will be adjusted depending on how long the simulations take to run if variabledt is active

        // TODO - decide if variable for fixed dt results in a more stable result
        protected readonly bool
            variabledt = false; // Set this to true to allow the predictor to choose a dt based on how long each run is taking, and false to use a fixed dt.

        public bool noSkipToFreefall = false;

        private readonly Queue readyResults = new Queue();

        private Random random;
        public  double maxOrbits = 1;

        private double lastSimTime;
        private double lastSimSteps;
        private double lastErrorSimTime;
        private double lastErrorSimSteps;

        [ValueInfoItem("#MechJeb_LandingSim", InfoItem.Category.Misc, showInEditor = false)] //LandingSim
        public string LandingSimTime() =>
            (stopwatch.ElapsedMilliseconds / 1000d).ToString("F1") + "/" + lastSimTime.ToString("F2") + " (" + lastSimSteps + ")\n"
            + (errorStopwatch.ElapsedMilliseconds / 1000d).ToString("F1") + "/" + lastErrorSimTime.ToString("F2") + " (" + lastErrorSimSteps +
            ")\n"
            + ReentrySimulation.ActiveDt.ToString("F2") + " " + ReentrySimulation.ActiveStep + "\n"
            + dt.ToString("F2") + " " + Time.fixedDeltaTime.ToString("F2") + " " + parachuteSemiDeployMultiplier.ToString("F3");

        public override void OnStart(PartModule.StartState state)
        {
            random = new Random();
            if (state != PartModule.StartState.None && state != PartModule.StartState.Editor)
            {
                Core.AddToPostDrawQueue(DoMapView);
            }
        }

        protected override void OnModuleEnabled() => TryStartSimulation(false);

        protected override void OnModuleDisabled()
        {
            stopwatch.Stop();
            stopwatch.Reset();

            errorStopwatch.Stop();
            errorStopwatch.Reset();
        }

        public override void OnFixedUpdate()
        {
            CheckForResult();

            TryStartSimulation(true);
        }

        private void TryStartSimulation(bool doErrorSim)
        {
            try
            {
                if (!Vessel.LandedOrSplashed)
                {
                    // We should be running simulations periodically. If one is not running right now,
                    // check if enough time has passed since the last one to start a new one:
                    if (!SimulationRunning && (stopwatch.ElapsedMilliseconds > millisecondsBetweenSimulations || !stopwatch.IsRunning))
                    {
                        // variabledt generate too much instability of the landing site with atmo.
                        // variabledt = !(mainBody.atmosphere && core.landing.enabled);
                        // the altitude may induce some instability but allow for greater precision of the display in manual flight

                        //variabledt = !mainBody.atmosphere || vessel.terrainAltitude < 1000 ;
                        //if (!variabledt)
                        //    dt = 0.5;

                        stopwatch.Stop();
                        stopwatch.Reset();

                        StartSimulation(false);
                    }

                    // We also periodically run simulations containing deliberate errors if we have been asked to do so by the landing autopilot.
                    if (doErrorSim && runErrorSimulations && !errorSimulationRunning &&
                        (errorStopwatch.ElapsedMilliseconds >= millisecondsBetweenErrorSimulations || !errorStopwatch.IsRunning))
                    {
                        errorStopwatch.Stop();
                        errorStopwatch.Reset();

                        StartSimulation(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public override void OnUpdate() => MaintainAerobrakeNode();

        protected void StartSimulation(bool addParachuteError)
        {
            double altitudeOfPreviousPrediction = 0;
            double parachuteMultiplierForThisSimulation = parachuteSemiDeployMultiplier;
            if (addParachuteError)
            {
                errorSimulationRunning = true;
                errorStopwatch.Start(); //starts a timer that times how long the simulation takes
            }
            else
            {
                SimulationRunning = true;
                stopwatch.Start(); //starts a timer that times how long the simulation takes
            }

            Orbit patch = GetReenteringPatch() ?? Orbit;
            // Work out what the landing altitude was of the last prediction, and use that to pass into the next simulation
            if (result != null)
            {
                if (result.Outcome == ReentrySimulation.Outcome.LANDED && result.Body != null)
                {
                    altitudeOfPreviousPrediction =
                        result.EndASL; // Note that we are caling GetResult here to force the it to calculate the endASL, if it has not already done this. It is not allowed to do this previously as we are only allowed to do it from this thread, not the reentry simulation thread.
                }
            }

            // Is this a simulation run with errors added? If so then add some error to the parachute multiple
            if (addParachuteError)
            {
                parachuteMultiplierForThisSimulation *= 1d + (random.Next(1000000) - 500000d) / 10000000d;
            }

            // The curves used for the sim are not thread safe so we need a copy used only by the thread
            var simCurves = ReentrySimulation.SimCurves.Borrow(patch.referenceBody);


            //if (descentSpeedPolicy != null)
            //    print(vesselState.limitedMaxThrustAccel.ToString("F2") + " " + descentSpeedPolicy.MaxAllowedSpeed(vesselState.CoM - mainBody.position, vesselState.surfaceVelocity).ToString("F2"));

            var simVessel = SimulatedVessel.Borrow(Vessel, simCurves, patch.StartUT, Core.Landing.Enabled && deployChutes ? limitChutesStage : -1);
            var sim = ReentrySimulation.Borrow(patch, patch.StartUT, simVessel, simCurves, descentSpeedPolicy, decelEndAltitudeASL,
                VesselState.limitedMaxThrustAccel, parachuteMultiplierForThisSimulation, altitudeOfPreviousPrediction, addParachuteError, dt,
                Time.fixedDeltaTime, maxOrbits, noSkipToFreefall);
            //MechJebCore.print("Sim ran with dt=" + dt.ToString("F3"));

            //Run the simulation in a separate thread
            ThreadPool.QueueUserWorkItem(RunSimulation, sim);
            //RunSimulation(sim);
        }

        private void RunSimulation(object o)
        {
            var sim = (ReentrySimulation)o;
            try
            {
                ReentrySimulation.Result newResult = sim.RunSimulation();

                lock (readyResults)
                {
                    readyResults.Enqueue(newResult);
                }

                if (newResult.MultiplierHasError)
                {
                    //see how long the simulation took
                    errorStopwatch.Stop();
                    long millisecondsToCompletion = errorStopwatch.ElapsedMilliseconds;
                    lastErrorSimTime  = millisecondsToCompletion * 0.001;
                    lastErrorSimSteps = newResult.Steps;

                    errorStopwatch.Reset();

                    //set the delay before the next simulation
                    millisecondsBetweenErrorSimulations = Math.Min(Math.Max(4 * millisecondsToCompletion, 400), 5);
                    // Note that we are going to run the simulations with error in less often that the real simulations

                    //start the stopwatch that will count off this delay
                    errorStopwatch.Start();
                    errorSimulationRunning = false;
                }
                else
                {
                    //see how long the simulation took
                    stopwatch.Stop();
                    long millisecondsToCompletion = stopwatch.ElapsedMilliseconds;
                    stopwatch.Reset();

                    //set the delay before the next simulation
                    millisecondsBetweenSimulations = Math.Min(Math.Max(2 * millisecondsToCompletion, 200), 5);
                    lastSimTime                    = millisecondsToCompletion * 0.001;
                    lastSimSteps                   = newResult.Steps;
                    // Do not wait for too long before running another simulation, but also give the processor a rest.

                    // How long should we set the max_dt to be in the future? Calculate for interationsPerSecond runs per second. If we do not enter the atmosphere, however do not do so as we will complete so quickly, it is not a good guide to how long the reentry simulation takes.
                    if (newResult.Outcome == ReentrySimulation.Outcome.AEROBRAKED ||
                        newResult.Outcome == ReentrySimulation.Outcome.LANDED)
                    {
                        if (variabledt)
                        {
                            dt = newResult.Maxdt * (millisecondsToCompletion / 1000d) / (1d / (3d * interationsPerSecond));
                            // There is no point in having a dt that is smaller than the physics frame rate as we would be trying to be more precise than the game.
                            dt = Math.Max(dt, sim.MinDT);
                            // Set a sensible upper limit to dt as well. - in this case 10 seconds
                            dt = Math.Min(dt, 10);
                        }
                    }

                    //Debug.Log("Result:" + this.result.outcome + " Time to run: " + millisecondsToCompletion + " millisecondsBetweenSimulations: " + millisecondsBetweenSimulations + " new dt: " + dt + " Time.fixedDeltaTime " + Time.fixedDeltaTime + "\n" + this.result.ToString()); // Note the endASL will be zero as it has not yet been calculated, and we are not allowed to calculate it from this thread :(

                    //start the stopwatch that will count off this delay
                    stopwatch.Start();
                    SimulationRunning = false;
                }
            }
            catch (Exception ex)
            {
                //Debug.Log(string.Format("Exception in MechJebModuleLandingPredictions.RunSimulation\n{0}", ex.StackTrace));
                //Debug.LogException(ex);
                Dispatcher.InvokeAsync(() => Debug.LogException(ex));
            }
            finally
            {
                sim.Release();
            }
        }

        private void CheckForResult()
        {
            lock (readyResults)
            {
                while (readyResults.Count > 0)
                {
                    var newResult = (ReentrySimulation.Result)readyResults.Dequeue();

                    // If running the simulation resulted in an error then just ignore it.
                    if (newResult.Outcome != ReentrySimulation.Outcome.ERROR)
                    {
                        if (newResult.Body != null)
                            newResult.EndASL = newResult.Body.TerrainAltitude(newResult.EndPosition.Latitude, newResult.EndPosition.Longitude);

                        if (newResult.MultiplierHasError)
                        {
                            if (errorResult != null)
                                errorResult.Release();
                            errorResult = newResult;
                        }
                        else
                        {
                            if (result != null)
                                result.Release();
                            result = newResult;
                        }
                    }
                    else
                    {
                        if (newResult.Exception != null)
                            Print("Exception in the last simulation\n" + newResult.Exception.Message + "\n" + newResult.Exception.StackTrace);
                        newResult.Release();
                    }
                }
            }
        }

        protected Orbit GetReenteringPatch()
        {
            Orbit patch = Orbit;

            int i = 0;

            do
            {
                i++;
                double reentryRadius = patch.referenceBody.Radius + patch.referenceBody.RealMaxAtmosphereAltitude();
                Orbit nextPatch = Vessel.GetNextPatch(patch, aerobrakeNode);
                if (patch.PeR < reentryRadius)
                {
                    if (patch.Radius(patch.StartUT) < reentryRadius) return patch;

                    double reentryTime = patch.NextTimeOfRadius(patch.StartUT, reentryRadius);
                    if (patch.StartUT < reentryTime && (nextPatch == null || reentryTime < nextPatch.StartUT))
                    {
                        return patch;
                    }
                }

                patch = nextPatch;
            } while (patch != null);

            return null;
        }

        protected void MaintainAerobrakeNode()
        {
            if (makeAerobrakeNodes)
            {
                //Remove node after finishing aerobraking:
                if (aerobrakeNode != null && Vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                {
                    if (aerobrakeNode.UT < VesselState.time && VesselState.altitudeASL > MainBody.RealMaxAtmosphereAltitude())
                    {
                        aerobrakeNode.RemoveSelf();
                        aerobrakeNode = null;
                    }
                }

                //Update or create node if necessary:
                ReentrySimulation.Result r = Result;
                if (r != null && r.Outcome == ReentrySimulation.Outcome.AEROBRAKED)
                {
                    //Compute the node dV:
                    Orbit preAerobrakeOrbit = GetReenteringPatch();

                    //Put the node at periapsis, unless we're past periapsis. In that case put the node at the current time.
                    double UT;
                    if (preAerobrakeOrbit == Orbit &&
                        VesselState.altitudeASL < MainBody.RealMaxAtmosphereAltitude() && VesselState.speedVertical > 0)
                    {
                        UT = VesselState.time;
                    }
                    else
                    {
                        UT = preAerobrakeOrbit.NextPeriapsisTime(preAerobrakeOrbit.StartUT);
                    }

                    Orbit postAerobrakeOrbit =
                        MuUtils.OrbitFromStateVectors(r.WorldAeroBrakePosition(), r.WorldAeroBrakeVelocity(), r.Body, r.AeroBrakeUT);

                    Vector3d dV = OrbitalManeuverCalculator.DeltaVToChangeApoapsis(preAerobrakeOrbit, UT, postAerobrakeOrbit.ApR);

                    if (aerobrakeNode != null && Vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                    {
                        //update the existing node
                        Vector3d nodeDV = preAerobrakeOrbit.DeltaVToManeuverNodeCoordinates(UT, dV);
                        aerobrakeNode.UpdateNode(nodeDV, UT);
                    }
                    else
                    {
                        //place a new node
                        aerobrakeNode = Vessel.PlaceManeuverNode(preAerobrakeOrbit, dV, UT);
                    }
                }
                else
                {
                    //no aerobraking, remove the node:
                    if (aerobrakeNode != null && Vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                    {
                        aerobrakeNode.RemoveSelf();
                    }
                }
            }
            else
            {
                //Remove aerobrake node when it is turned off:
                if (aerobrakeNode != null && Vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                {
                    aerobrakeNode.RemoveSelf();
                }
            }
        }

        private void DoMapView()
        {
            if ((MapView.MapIsEnabled || camTrajectory) && !Vessel.LandedOrSplashed && Enabled)
            {
                ReentrySimulation.Result drawnResult = Result;
                if (drawnResult != null)
                {
                    if (drawnResult.Outcome == ReentrySimulation.Outcome.LANDED)
                        GLUtils.DrawGroundMarker(drawnResult.Body, drawnResult.EndPosition.Latitude, drawnResult.EndPosition.Longitude, Color.blue,
                            MapView.MapIsEnabled, 60);

                    if (showTrajectory && drawnResult.Outcome != ReentrySimulation.Outcome.ERROR &&
                        drawnResult.Outcome != ReentrySimulation.Outcome.NO_REENTRY)
                    {
                        double interval = Math.Max(Math.Min((drawnResult.EndUT - drawnResult.InputUT) / 1000, 10), 0.1);
                        //using (var list = drawnResult.WorldTrajectory(interval, worldTrajectory && MapView.MapIsEnabled))
                        using (Disposable<List<Vector3d>> list = drawnResult.WorldTrajectory(interval, worldTrajectory))
                        {
                            if (!MapView.MapIsEnabled && (noSkipToFreefall || Vessel.staticPressurekPa > 0))
                                list.value[0] = VesselState.CoM;
                            GLUtils.DrawPath(drawnResult.Body, list.value, Color.red, MapView.MapIsEnabled);
                        }
                    }
                }
            }
        }

        public MechJebModuleLandingPredictions(MechJebCore core) : base(core) { }
    }
}
