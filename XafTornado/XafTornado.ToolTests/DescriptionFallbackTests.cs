using System.ComponentModel;
using XafTornado.Module.Attributes;
using XafTornado.Module.Services;
using Xunit;

namespace XafTornado.ToolTests;

// No fixture and no collection: these read attributes off local types, so they need neither the
// app nor the database. They cover the one decision in DescriptionOf — which attribute wins.

public class DescriptionFallbackTests
{
    [AIDescription("Written for the assistant")]
    [Description("Written for the UI")]
    private sealed class Both;

    [Description("What this class is, in the UI")]
    private sealed class OnlyDescription
    {
        [Description("How much this customer may owe at once")]
        public decimal CreditLimit { get; set; }
    }

    private sealed class Neither
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void AIDescriptionWinsWhenBothAreWritten() =>
        Assert.Equal("Written for the assistant", SchemaDiscoveryService.DescriptionOf(typeof(Both)));

    [Fact]
    public void DescriptionIsReadWhenAIDescriptionIsAbsent() =>
        Assert.Equal("What this class is, in the UI",
            SchemaDiscoveryService.DescriptionOf(typeof(OnlyDescription)));

    [Fact]
    public void APropertyIsReadTheSameWay() =>
        Assert.Equal("How much this customer may owe at once",
            SchemaDiscoveryService.DescriptionOf(
                typeof(OnlyDescription).GetProperty(nameof(OnlyDescription.CreditLimit))));

    [Fact]
    public void NeitherAttributeReadsAsNothing()
    {
        Assert.Null(SchemaDiscoveryService.DescriptionOf(typeof(Neither)));
        Assert.Null(SchemaDiscoveryService.DescriptionOf(
            typeof(Neither).GetProperty(nameof(Neither.Name))));
    }
}
