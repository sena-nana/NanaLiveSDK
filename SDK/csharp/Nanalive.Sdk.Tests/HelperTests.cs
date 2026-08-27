using Nanalive.Sdk;
using Xunit;

namespace Nanalive.Sdk.Tests;

public class HelperTests
{
    [Fact]
    public void ExecutableHotkeys_FiltersOnExecutableFlag()
    {
        var hotkeys = new[]
        {
            Mp.Map(("hotkeyID", Mp.Str("h1")), ("executable", Mp.Bool(true))),
            Mp.Map(("hotkeyID", Mp.Str("h2")), ("executable", Mp.Bool(false))),
            Mp.Map(("hotkeyID", Mp.Str("h3"))),
        };
        var executable = Helpers.ExecutableHotkeys(hotkeys);

        var id = Assert.Single(executable);
        Assert.Equal("h1", id.GetField("hotkeyID")!.TryString());
    }

    [Fact]
    public void ParameterValueAfterTicks_ClampsToRange()
    {
        // 每格 0.5（量程 0..20 除以 40）。
        var parameter = Mp.Map(
            ("value", Mp.Num(10.0)), ("min", Mp.Num(0.0)), ("max", Mp.Num(20.0)));
        Assert.Equal(10.0, Helpers.ParameterValueAfterTicks(parameter, 0.0));
        Assert.Equal(12.0, Helpers.ParameterValueAfterTicks(parameter, 4.0));
        Assert.Equal(8.0, Helpers.ParameterValueAfterTicks(parameter, -4.0));
        Assert.Equal(20.0, Helpers.ParameterValueAfterTicks(parameter, 400.0));
        Assert.Equal(0.0, Helpers.ParameterValueAfterTicks(parameter, -400.0));
        Assert.Equal(10.0, Helpers.ParameterValueAfterTicks(parameter, double.NaN));
        // 无参数回退 0，span 为 0 时步长为 1，但仍钳制在 min==max 上。
        Assert.Equal(0.0, Helpers.ParameterValueAfterTicks(null, 3.0));
        var flat = Mp.Map(("value", Mp.Num(7.0)), ("min", Mp.Num(5.0)), ("max", Mp.Num(5.0)));
        Assert.Equal(5.0, Helpers.ParameterValueAfterTicks(flat, 2.0));
    }

    [Fact]
    public void WriteParameterCommand_ValidatesInput()
    {
        var command = Helpers.WriteParameterCommand("ParamA", 3.5);
        Assert.NotNull(command);
        Assert.Equal("ParameterWriteRequest", command!.GetField("messageType")!.TryString());
        var parameters = command.GetField("data")!.GetField("parameters")!;
        Assert.Equal(3.5, parameters.GetField("ParamA")!.TryNumber());

        Assert.Null(Helpers.WriteParameterCommand(null, 1.0));
        Assert.Null(Helpers.WriteParameterCommand("", 1.0));
        Assert.Null(Helpers.WriteParameterCommand("ParamA", double.NaN));
    }
}
