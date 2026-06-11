// -----------------------------------------------------------------------
// <copyright file="Configuration.cs" company="">
// Triangle.NET Copyright (c) 2012-2022 Christian Woltering
// </copyright>
// -----------------------------------------------------------------------

namespace TriangleNet
{
    using System;

    /// <summary>
    /// Configure advanced aspects of the library.
    /// </summary>
    public class Configuration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Configuration" /> class.
        /// </summary>
        public Configuration()
            : this(() => RobustPredicates.Default, () => new TrianglePool())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Configuration" /> class.
        /// </summary>
        /// <param name="predicates">Factory method for <see cref="IPredicates" />.</param>
        public Configuration(Func<IPredicates> predicates)
            : this(predicates, () => new TrianglePool())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Configuration" /> class.
        /// </summary>
        /// <param name="predicates">Factory method for <see cref="IPredicates" />.</param>
        /// <param name="trianglePool">Factory method for <see cref="TriangleNet.TrianglePool" />.</param>
        public Configuration(Func<IPredicates> predicates, Func<TrianglePool> trianglePool)
        {
            Predicates = predicates;
            TrianglePool = trianglePool;
            // Deterministic by default: the upstream `new Random()` is time-seeded, which makes the
            // point-location sampler — and therefore Steiner insertion and mesh topology — vary run
            // to run. For terrain/grading that means the SAME input can triangulate (and split) one
            // way on one solve and differently on the next. A fixed seed makes the whole pipeline
            // reproducible so grading output is stable and regressions are testable.
            RandomSource = () => new Random(0);
        }

        /// <summary>
        /// Gets or sets the factory method for the <see cref="IPredicates"/> implementation.
        /// </summary>
        public Func<IPredicates> Predicates { get; set; }

        /// <summary>
        /// Gets or sets the factory method for the <see cref="TriangleNet.TrianglePool"/>.
        /// </summary>
        public Func<TrianglePool> TrianglePool { get; set; }

        /// <summary>
        /// Gets or sets the factory method for a <see cref="Random"/> source.
        /// </summary>
        public Func<Random> RandomSource { get; set; }
    }
}
