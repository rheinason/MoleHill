using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionSamplingTests
{
    [Fact]
    public void NewSectionAlongCurve_SamplesAutomatically()
    {
        Assert.Equal(0.0, new LongitudinalSectionAnnotationDefinition().SampleInterval);
    }

    [Fact]
    public void NormalizeAfterLoad_SavedInterval_IsKept_AndBlankStaysAuto()
    {
        var saved = new LongitudinalSectionAnnotationDefinition { SampleInterval = 1.0 };
        var auto = new LongitudinalSectionAnnotationDefinition { SampleInterval = 0.0 };
        var negative = new LongitudinalSectionAnnotationDefinition { SampleInterval = -2.0 };
        var units = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

        saved.NormalizeAfterLoad(units, Guid.NewGuid());
        auto.NormalizeAfterLoad(units, Guid.NewGuid());
        negative.NormalizeAfterLoad(units, Guid.NewGuid());

        Assert.Equal(1.0, saved.SampleInterval);
        Assert.Equal(0.0, auto.SampleInterval);
        Assert.Equal(0.0, negative.SampleInterval);
    }
}
