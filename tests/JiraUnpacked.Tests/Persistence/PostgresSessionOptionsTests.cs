using JiraUnpacked.Infrastructure.Persistence;
using Npgsql;

namespace JiraUnpacked.Tests.Persistence;

public sealed class PostgresSessionOptionsTests
{
    private static string? OptionsOf(string cs) => new NpgsqlConnectionStringBuilder(cs).Options;

    [Fact]
    public void Adds_jit_off_to_a_string_without_options()
    {
        var cs = PostgresSessionOptions.WithoutJit("Host=localhost;Database=x;Username=u");
        Assert.Equal("-c jit=off", OptionsOf(cs));
        Assert.Equal("x", new NpgsqlConnectionStringBuilder(cs).Database);
    }

    [Fact]
    public void Keeps_options_already_there()
    {
        var cs = PostgresSessionOptions.WithoutJit("Host=localhost;Options=-c default_transaction_read_only=on");
        Assert.Equal("-c default_transaction_read_only=on -c jit=off", OptionsOf(cs));
    }

    [Fact]
    public void An_explicit_jit_setting_wins()
    {
        const string cs = "Host=localhost;Options=-c jit=on";
        Assert.Equal(cs, PostgresSessionOptions.WithoutJit(cs));
    }
}
