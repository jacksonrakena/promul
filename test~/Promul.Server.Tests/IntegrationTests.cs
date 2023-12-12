using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Promul.Relay.Server;
using Promul.Relay.Server.Models.Sessions;
using Promul.Relay.Server.Relay;
namespace Promul.Server.Tests;

[TestFixture]
public class IntegrationTests
{
    private WebApplicationFactory<Program> _factory;
    
    [SetUp]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>();
    }

    [TearDown]
    public void Teardown()
    {
        _factory.Dispose();
    }

    [Test]
    public async Task Test_Get_Creates_Session()
    {
        var client = _factory.CreateClient();
        var rs = _factory.Services.GetRequiredService<RelayServer>();
        Assert.That(rs.GetAllSessions(), Is.Empty);
        var response = await client.PutAsync("/session/create", null);
        response.EnsureSuccessStatusCode();
        var r = await response.Content.ReadFromJsonAsync<SessionInfo>();
        Assert.That(r.JoinCode, Is.Not.Empty);

        Assert.That(rs.GetAllSessions(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Test_Can_Join_Session()
    {
        var client = _factory.CreateClient();
        var rs = _factory.Services.GetRequiredService<RelayServer>();
        Assert.That(rs.GetAllSessions(), Is.Empty);
        var response = await client.PutAsync("/session/create", null);
        response.EnsureSuccessStatusCode();
        var r = await response.Content.ReadFromJsonAsync<SessionInfo>();
        Assert.That(r.JoinCode, Is.Not.Empty);
        Assert.That(rs.GetAllSessions(), Has.Count.EqualTo(1));

        var joinResponse = await client.PutAsync("/session/join", JsonContent.Create(new { joinCode = r.JoinCode }));
        joinResponse.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Test_That_Client_Cannot_Join_Nonexistent_Session()
    {
        var client = _factory.CreateClient();
        var rs = _factory.Services.GetRequiredService<RelayServer>();
        Assert.That(rs.GetAllSessions(), Is.Empty);
        var response = await client.PutAsync("/session/create", null);
        response.EnsureSuccessStatusCode();
        var r = await response.Content.ReadFromJsonAsync<SessionInfo>();
        Assert.That(r.JoinCode, Is.Not.Empty);
        Assert.That(rs.GetAllSessions(), Has.Count.EqualTo(1));
        
        var invalidJoin = await client.PutAsync("/session/join", JsonContent.Create(new { joinCode = "invalid" }));
        Assert.That(invalidJoin.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}