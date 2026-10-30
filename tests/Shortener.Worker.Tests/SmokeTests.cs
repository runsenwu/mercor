namespace Shortener.Worker.Tests;

public class SmokeTests
{
    [Fact]
    public void Shared_contract_names_are_stable()
    {
        Assert.Equal("clicks.analytics", Shared.ClickTopology.Queue);
    }
}
