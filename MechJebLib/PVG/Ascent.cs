/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */

using System;
using System.Collections.Generic;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using static MechJebLib.Utils.Statics;
using static System.Math;

namespace MechJebLib.PVG
{
    /// <summary>
    /// </summary>
    public partial class Ascent
    {
        private readonly AscentBuilder _input;
        private          double        _eccT;
        private          double        _gammaT;
        private          Optimizer?    _optimizer;
        private          double        _smaT;

        private double _vT;

        private Ascent(AscentBuilder builder)
        {
            _input = builder;
        }

        private List<Phase> _phases              => _input._phases;
        private V3          _r0                  => _input._r0;
        private V3          _v0                  => _input._v0;
        private V3          _u0                  => _input._u0;
        private double      _t0                  => _input._t0;
        private double      _mu                  => _input._mu;
        private double      _rbody               => _input._rbody;
        private double      _peR                 => _input._peR;
        private double      _apR                 => _input._apR;
        private double      _attR                => _input._attR;
        private double      _incT                => _input._incT;
        private double      _lanT                => _input._lanT;
        private double      _fpaT                => _input._fpaT;
        private double      _hT                  => _input._hT;
        private bool        _attachAltFlag       => _input._attachAltFlag;
        private bool        _lanflag             => _input._lanflag;
        private bool        _fixedBurnTime       => _input._fixedBurnTime;
        private Solution?   _solution            => _input._solution;
        private int         _optimizedPhase      => _input._optimizedPhase;
        private int         _optimizedCoastPhase => _input._optimizedCoastPhase;

        public void Run()
        {
            (_smaT, _eccT) = Astro.SmaEccFromApsides(_peR, _apR);

            using Optimizer.OptimizerBuilder builder = Optimizer.Builder()
                .Initial(_r0, _v0, _u0, _t0, _mu, _rbody)
                .TerminalConditions(_hT);

            if (_solution == null)
            {
                _optimizer = _fixedBurnTime
                    ? InitialBootstrappingFixed(builder)
                    : InitialBootstrappingOptimized(builder);
            }
            else
            {
                _optimizer = ConvergedOptimization(builder, _solution);
            }
        }

        public void Test(V3 pv0, V3 pr0)
        {
            (_smaT, _eccT) = Astro.SmaEccFromApsides(_peR, _apR);

            using Optimizer.OptimizerBuilder builder = Optimizer.Builder()
                .Initial(_r0, _v0, _u0, _t0, _mu, _rbody)
                .TerminalConditions(_hT);

            ForceNumericalIntegration();

            if (_fixedBurnTime)
            {
                ApplyEnergy(builder);
            }
            else
            {
                if (_attachAltFlag || _eccT < 1e-4)
                    ApplyFPA(builder);
                else
                    ApplyKepler(builder);
            }

            using Optimizer pvg = builder.Build(_phases);
            pvg.Bootstrap(pv0, pr0);
            pvg.Run();

            _optimizer = pvg;
        }

        public Optimizer? GetOptimizer() => _optimizer;

        private Optimizer ConvergedOptimization(Optimizer.OptimizerBuilder builder, Solution solution)
        {
            ForceNumericalIntegration();

            if (_fixedBurnTime)
            {
                ApplyEnergy(builder);
            }
            else
            {
                if (_attachAltFlag || _eccT < 1e-4)
                    ApplyFPA(builder);
                else
                    ApplyKepler(builder);
            }

            ApplyOldBurnTimesToPhases(solution);
            Optimizer pvg = builder.Build(_phases);
            pvg.Bootstrap(solution);
            pvg.Run();

            if (!pvg.Success())
                throw new Exception("converged optimizer failed");

            using Solution solution2 = pvg.GetSolution();

            (V3 rf, V3 vf) = solution2.TerminalStateVectors();

            (_, _, _, _, _, double tanof, _) =
                Astro.KeplerianFromStateVectors(_mu, rf, vf);

            if (_attachAltFlag || _fixedBurnTime || Abs(ClampPi(tanof)) < PI / 2.0)
                return pvg;

            ApplyFPA(builder);
            ApplyOldBurnTimesToPhases(solution2);

            using Optimizer pvg2 = builder.Build(_phases);
            pvg2.Bootstrap(solution2);
            pvg2.Run();

            return pvg2.Success() ? pvg2 : pvg;
        }

        private void ApplyFPA(Optimizer.OptimizerBuilder builder)
        {
            // If _attachAltFlag is NOT set then we are bootstrapping with ApplyFPA prior to
            // trying free attachment with Kepler and attR is invalid and we need to fix to
            // the PeR.  This should be fixed in the AscentBuilder by having more APIs than
            // just "SetTarget" that fixes this correctly there.
            double attR = _attachAltFlag ? _attR : _peR;

            (_vT, _gammaT) = Astro.ConvertApsidesTargetToFPA(_peR, _apR, attR, _mu);
            if (_lanflag)
                builder.TerminalFPA5(attR, _vT, _gammaT, _incT, _lanT);
            else
                builder.TerminalFPA4(attR, _vT, _gammaT, _incT);
        }

        private void ApplyKepler(Optimizer.OptimizerBuilder builder)
        {
            (_smaT, _eccT) = Astro.SmaEccFromApsides(_peR, _apR);

            if (_lanflag)
                builder.TerminalKepler4(_smaT, _eccT, _incT, _lanT);
            else
                builder.TerminalKepler3(_smaT, _eccT, _incT);
        }

        private void ApplyEnergy(Optimizer.OptimizerBuilder builder)
        {
            if (_lanflag)
                builder.TerminalEnergy4(_attR, _incT, _lanT);
            else
                builder.TerminalEnergy3(_attR, _fpaT, _incT);
        }

        private Optimizer InitialBootstrappingFixed(Optimizer.OptimizerBuilder builder)
        {
            ApplyEnergy(builder);

            // guess the initial launch direction
            V3 enu = Astro.ENUHeadingForInclination(_incT, _r0);
            enu.z = 1.0; // add 45 degrees up
            V3 pvGuess = Astro.ENUToECI(_r0, enu).normalized;

            List<Phase> bootphases = DupPhases(_phases);

            // FIXME: we may want to convert this to an optimized burntime circular orbit problem with an infinite upper stage for bootstrapping
            for (int p = 0; p < bootphases.Count; p++)
            {
                bootphases[p].Unguided = false;

                if (p == _optimizedCoastPhase)
                {
                    bootphases[p].OptimizeTime = false;
                    // FIXME: a problem here is that if we require a coast to hit the target then we'll never converge
                    bootphases[p].bt = 0;
                }
            }

            using Optimizer pvg = builder.Build(bootphases);
            pvg.Bootstrap(pvGuess, _r0.normalized);
            pvg.Run();

            if (!pvg.Success())
                throw new Exception("Target unreachable (fixed bootstrapping)");

            using Solution solution = pvg.GetSolution();
            ApplyOldBurnTimesToPhases(solution);

            List<Phase> bootphases2 = DupPhases(_phases);

            if (_optimizedCoastPhase > -1)
            {
                double total = 0.0;

                for (int i = 0; i < bootphases2.Count; i++)
                    total += bootphases2[i].bt;

                bootphases2[_optimizedCoastPhase].bt = total;
            }

            Optimizer pvg2 = builder.Build(bootphases2);
            pvg2.Bootstrap(solution);
            pvg2.Run();

            if (!pvg2.Success())
                throw new Exception("Target unreachable");

            return pvg2;
        }

        private Optimizer InitialBootstrappingOptimized(Optimizer.OptimizerBuilder builder)
        {
            /*
             * Analytic initial bootstrapping with infinite upper stage and no coast, forced FPA attachment
             */

            ApplyFPA(builder);

            // guess the initial launch direction
            V3 enu = Astro.ENUHeadingForInclination(_incT, _r0);
            enu.z = 1.0; // add 45 degrees up
            V3 pvGuess = Astro.ENUToECI(_r0, enu).normalized;

            List<Phase> bootphases = DupPhases(_phases);

            // set the coast phase to fixed time of zero
            if (_optimizedCoastPhase > -1)
            {
                bootphases[_optimizedCoastPhase].OptimizeTime = false;
                bootphases[_optimizedCoastPhase].bt           = 0;
            }

            // switch the optimized phase to the top stage of the rocket
            bootphases[_optimizedPhase].OptimizeTime = false;

            // set the top stage to infinite + optimized + unguided
            bootphases[bootphases.Count - 1].Infinite     = true;
            bootphases[bootphases.Count - 1].Unguided     = false;
            bootphases[bootphases.Count - 1].OptimizeTime = true;

            DebugPrint("*** PHASE 1: DOING INITIAL INFINITE UPPER STAGE ***");
            using Optimizer pvg = builder.Build(bootphases);
            pvg.Bootstrap(pvGuess, _r0.normalized);
            pvg.Run();

            if (!pvg.Success())
                throw new Exception("Target unreachable (infinite ISP)");

            /*
             * Analytic bootstrapping with finite upper stage and coast, forced FPA attachment
             */

            using Solution solution = pvg.GetSolution();
            ApplyOldBurnTimesToPhases(solution);

            if (_optimizedCoastPhase > -1)
            {
                double total = 0.0;

                for (int i = 0; i < _phases.Count; i++)
                    total += _phases[i].bt;

                _phases[_optimizedCoastPhase].OptimizeTime = true;
                _phases[_optimizedCoastPhase].bt           = total;
            }

            DebugPrint("*** PHASE 2: ADDING COAST AND FINITE UPPER STAGE ***");
            using Optimizer pvg2 = builder.Build(_phases);
            pvg2.Bootstrap(solution);
            pvg2.Run();

            /*
             * Numerical re-bootstrapping with finite upper stage and coast, forced FPA attachment
             */

            ForceNumericalIntegration();
            Optimizer pvg3 = builder.Build(_phases);
            if (pvg2.Success())
            {
                DebugPrint("*** PHASE 3: REDOING WITH NUMERICAL INTEGRATION ***");
                using Solution solution2 = pvg2.GetSolution();
                pvg3.Bootstrap(solution2);
                pvg3.Run();

                if (!pvg3.Success())
                    throw new Exception("Target unreachable after analytic convergence");
            }
            else
            {
                DebugPrint("*** PHASE 3: ATTEMPTING NUMERICAL INTEGRATION AFTER ANALYTICAL FAILURE ***");
                pvg3.Bootstrap(solution);
                pvg3.Run();

                if (!pvg3.Success())
                    throw new Exception("Target unreachable");
            }

            if (_attachAltFlag || _eccT < 1e-4)
                return pvg3;

            /*
             * Numerical re-bootstrapping with finite upper stage and coast, free attachment
             */

            using Solution solution3 = pvg3.GetSolution();

            ApplyKepler(builder);
            ApplyOldBurnTimesToPhases(solution3);

            DebugPrint("*** PHASE 4: RELAXING TO FREE ATTACHMENT ***");
            Optimizer pvg4 = builder.Build(_phases);
            pvg4.Bootstrap(solution3);
            pvg4.Run();

            if (!pvg4.Success())
            {
                DebugPrint("*** FREE ATTACHMENT FAILED, FALLING BACK TO PERIAPSIS ***");
                return pvg3;
            }

            /*
             * Sanity check to ensure free attachment did not attach to the apoapsis
             */

            using Solution solution4 = pvg4.GetSolution();

            if (solution3.Vgo(solution3.T0) < solution4.Vgo(solution4.T0))
            {
                // this probably means apoapsis attachment or wrong side of the periapsis
                DebugPrint(
                    $"*** PERIAPSIS ATTACHMENT IS MORE OPTIMAL ({solution3.Vgo(solution3.T0)} < {solution4.Vgo(solution4.T0)}) THAN FREE ATTACHMENT SOLN ***");
                return pvg3;
            }

            return pvg4;
        }

        private void ForceNumericalIntegration()
        {
            for (int i = 0; i < _phases.Count; i++)
                _phases[i].Analytic = false;
        }

        private void ApplyOldBurnTimesToPhases(Solution oldSolution)
        {
            for (int i = 0; i < _phases.Count; i++)
            {
                if (!_phases[i].OptimizeTime)
                    continue;

                for (int j = 0; j < oldSolution.Segments; j++)
                {
                    if (!oldSolution.OptimizeTime(j))
                        continue;

                    if (oldSolution.CoastPhase(j) != _phases[i].Coast)
                        continue;

                    _phases[i].bt = Min(oldSolution.Bt(j, _t0), _phases[i].tau * 0.99);
                }
            }
        }

        // FIXME: this obviously creates garbage and needs to return an IDisposable wrapping List<Phases> that has a pool
        private List<Phase> DupPhases(List<Phase> oldphases)
        {
            var newphases = new List<Phase>();

            foreach (Phase phase in oldphases)
                newphases.Add(phase.DeepCopy());

            return newphases;
        }

        public static AscentBuilder Builder() => new AscentBuilder();
    }
}
