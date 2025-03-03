﻿using System;
using System.Collections.Generic;
using MechJebLib.Functions;
using MechJebLib.Lambert;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;
using static MechJebLib.Utils.Statics;
using static System.Math;

namespace MechJebLibTest.LambertTests
{
    public class GoodingTests
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public GoodingTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        [Fact]
        private void SingleRevolution2()
        {
            const int NTRIALS = 500;

            var random = new Random();

            for (int i = 0; i < NTRIALS; i++)
            {
                var    r0 = new V3(4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2);
                var    v0 = new V3(4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2);
                double dt;
                double ecc = Astro.EccFromStateVectors(1.0, r0, v0);

                if (ecc < 1)
                    dt = random.NextDouble() * Astro.PeriodFromStateVectors(1.0, r0, v0);
                else
                    dt = random.NextDouble() * 5;

                (V3 r1, V3 v1) = Shepperd.Solve(1.0, dt, r0, v0);
                (V3 vi, V3 vf) = Gooding.Solve(1.0, r0, v0, r1, dt, 0);

                // most of the time we get 1e-10 accuracy, but not always
                vi.ShouldEqual(v0, 1e-4);
                vf.ShouldEqual(v1, 1e-4);
            }
        }

        [Fact]
        private void SingleRevolution3()
        {
            const int NTRIALS = 500;

            var random = new Random();

            var izzo = new Izzo();

            for (int i = 0; i < NTRIALS; i++)
            {
                var    r0 = new V3(4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2);
                var    v0 = new V3(4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2, 4 * random.NextDouble() - 2);
                double dt;
                double ecc = Astro.EccFromStateVectors(1.0, r0, v0);

                if (ecc < 1)
                    dt = random.NextDouble() * Astro.PeriodFromStateVectors(1.0, r0, v0);
                else
                    dt = random.NextDouble() * 5;

                (V3 r1, V3 v1) = Shepperd.Solve(1.0, dt, r0, v0);
                (V3 vi, V3 vf) = izzo.Solve(1.0, r0, v0, r1, dt, 0);

                // most of the time we get 1e-10 accuracy, but not always
                vi.ShouldEqual(v0, 1e-4);
                vf.ShouldEqual(v1, 1e-4);
            }
        }

        [Fact]
        private void SingleRevolution()
        {
            double mu = 1.0;

            for (int j = 0; j < 40; j++)
            {
                double ecc = 0.1 * j;
                double sma = ecc > 1 ? -1.0 : 1.0;

                _testOutputHelper.WriteLine($"{ecc}");

                var elist = new List<double>(); // eccentric anomaly
                var tlist = new List<double>(); // time of flight
                var rlist = new List<double>(); // magnitude of r
                var vlist = new List<double>(); // mangitude of v
                var flist = new List<double>(); // true anomaly

                for (int i = 0; i < 360; i += 4)
                {
                    double eanom = Deg2Rad(i);

                    double time = Astro.TimeSincePeriapsisFromEccentricAnomaly(mu, sma, ecc, eanom);

                    double tanom = Astro.TrueAnomalyFromEccentricAnomaly(ecc, eanom);

                    double smp = ecc == 1 ? 2 * sma : sma * (1.0 - ecc * ecc);

                    double energy = ecc != 1 ? -1.0 / (2.0 * sma) : 0;

                    double r = smp / (1.0 + ecc * Cos(tanom));

                    double v = Sqrt(2 * (energy + 1.0 / r));

                    elist.Add(eanom);
                    tlist.Add(time);
                    rlist.Add(r);
                    vlist.Add(v);
                    flist.Add(tanom);
                }

                double diffmax = 0;

                for (int n1 = 0; n1 < elist.Count; n1++)
                {
                    for (int n2 = n1 + 1; n2 < elist.Count; n2++)
                    {
                        double VR11, VT11, VR12, VT12;

                        (_, VR11, VT11, VR12, VT12, _, _, _, _) =
                            Gooding.VLAMB(1.0, rlist[n1], rlist[n2], flist[n2] - flist[n1], tlist[n2] - tlist[n1]);
                        double vi    = Sqrt(VR11 * VR11 + VT11 * VT11);
                        double vf    = Sqrt(VR12 * VR12 + VT12 * VT12);
                        double diff1 = vlist[n1] - vi;
                        double diff2 = vlist[n2] - vf;
                        double diff  = Sqrt(diff1 * diff1 + diff2 * diff2);
                        if (diff > diffmax)
                        {
                            diffmax = diff;
                        }
                    }
                }

                _testOutputHelper.WriteLine($"{diffmax}");

                diffmax.ShouldBeZero(1e-10);
            }
        }
    }
}
