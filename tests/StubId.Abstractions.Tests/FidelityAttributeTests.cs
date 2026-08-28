namespace StubId.Abstractions.Tests;

public class FidelityAttributeTests
{
    [Fact]
    public void Live_verification_needs_a_fixture()
    {
        var withoutFixture = new FidelityAttribute(
            FidelityTier.Exact, FidelityProvenance.VerifiedLive);
        var withFixture = new FidelityAttribute(
            FidelityTier.Exact, FidelityProvenance.VerifiedLive)
        {
            Evidence = "fixtures/neb/pp/CAP-001",
        };

        Assert.False(withoutFixture.IsComplete);
        Assert.True(withFixture.IsComplete);
    }

    [Theory]
    [InlineData(FidelityProvenance.Divergent)]
    [InlineData(FidelityProvenance.NotEmulated)]
    public void Differing_from_the_broker_needs_a_stated_reason(FidelityProvenance provenance)
    {
        // The reason is what a 501 response links to, so an unexplained divergence would
        // leave a caller with no way to find out why their request failed.
        var unexplained = new FidelityAttribute(FidelityTier.OutOfContract, provenance);
        var explained = new FidelityAttribute(FidelityTier.OutOfContract, provenance)
        {
            Reason = "docs/brokers/neb/divergences.md#pades",
        };

        Assert.False(unexplained.IsComplete);
        Assert.True(explained.IsComplete);
    }

    [Theory]
    [InlineData(FidelityProvenance.DocsConflict)]
    [InlineData(FidelityProvenance.Assumed)]
    public void Unverified_knowledge_names_the_recording_that_would_settle_it(
        FidelityProvenance provenance)
    {
        var open = new FidelityAttribute(FidelityTier.Exact, provenance);
        var tracked = new FidelityAttribute(FidelityTier.Exact, provenance)
        {
            AwaitingCapture = "CAP-020",
        };

        Assert.False(open.IsComplete);
        Assert.True(tracked.IsComplete);
    }

    [Fact]
    public void Documented_and_uncontradicted_is_enough_on_its_own()
    {
        var attribute = new FidelityAttribute(
            FidelityTier.Shape, FidelityProvenance.DocsConfirmed);

        Assert.True(attribute.IsComplete);
    }
}
