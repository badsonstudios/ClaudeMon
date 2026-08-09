namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class ServiceStatusTextTests
{
    [Fact]
    public void Compose_NoStatus_ReturnsNull()
    {
        Assert.Null(ServiceStatusText.Compose(null));
    }

    [Fact]
    public void Compose_Operational_ReturnsNull()
    {
        // Healthy adds no visual noise: the flyout omits the line entirely.
        var status = new ServiceStatus(ServiceStatusLevel.Operational, "All Systems Operational");

        Assert.Null(ServiceStatusText.Compose(status));
    }

    [Theory]
    [InlineData(ServiceStatusLevel.Maintenance)]
    [InlineData(ServiceStatusLevel.Minor)]
    [InlineData(ServiceStatusLevel.Major)]
    [InlineData(ServiceStatusLevel.Critical)]
    public void Compose_NonOperational_EchoesThePagesOwnWording(ServiceStatusLevel level)
    {
        var line = ServiceStatusText.Compose(new ServiceStatus(level, "Partial System Outage"));

        Assert.NotNull(line);
        Assert.Contains("Anthropic", line);
        Assert.Contains("Partial System Outage", line);
    }

    [Fact]
    public void Compose_VeryLongDescription_IsTrimmed()
    {
        var description = new string('x', ServiceStatusText.MaxDescriptionLength + 40);

        var line = ServiceStatusText.Compose(new ServiceStatus(ServiceStatusLevel.Major, description));

        Assert.NotNull(line);
        Assert.EndsWith("…", line);
        Assert.DoesNotContain(description, line);
    }

    [Fact]
    public void Trim_DescriptionAtTheLimit_IsUntouched()
    {
        var description = new string('x', ServiceStatusText.MaxDescriptionLength);

        Assert.Equal(description, ServiceStatusText.Trim(description));
    }
}
