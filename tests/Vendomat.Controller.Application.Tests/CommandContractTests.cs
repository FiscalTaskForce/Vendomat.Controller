using Vendomat.Controller.Application.Contracts;
using Xunit;

namespace Vendomat.Controller.Application.Tests;

public sealed class CommandContractTests
{
    [Fact]
    public void RemoteCommandsCarryOptionalCommandIdForIdempotency()
    {
        var commandId = Guid.NewGuid();

        Assert.Equal(commandId, new RemoteCreditRequest { CommandId = commandId }.CommandId);
        Assert.Equal(commandId, new SanitationRequest { CommandId = commandId }.CommandId);
        Assert.Equal(commandId, new DispenseCommand { CommandId = commandId }.CommandId);
    }
}
