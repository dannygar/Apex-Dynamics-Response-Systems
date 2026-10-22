using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Evaluation.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Evaluation.Tests;

public sealed class BearerApiFactory : WebApplicationFactory<Program>
{
    public const string Tenant = "d22c4a39-e6d4-4e91-9c6d-d89b33323bb4";
    public const string Audience = "api://fixture-api";
    public const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    // Synthetic key belongs exclusively to test tokens and is not a production credential.
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("synthetic-test-signing-material-32-bytes-minimum"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public BearerApiFactory() { connection.Open(); }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Entra:TenantId", Tenant).UseSetting("Entra:Audience", Audience);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<EvaluationDb>>();
            services.AddDbContext<EvaluationDb>(o => o.UseSqlite(connection));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(Key);
                options.TokenValidationParameters.IssuerSigningKey = Key;
            });
        });
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) connection.Dispose(); }
}

public sealed class JwtTests : IDisposable
{
    private readonly BearerApiFactory factory = new();
    private readonly HttpClient client;
    public JwtTests()
    {
        client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<EvaluationDb>().Database.EnsureCreated();
    }
    private static string Token(string issuer, string audience, DateTime expires, SecurityKey? key = null, string grantedScope = "Evaluation.Access") =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
            [new Claim("tid", BearerApiFactory.Tenant), new Claim("oid", Guid.NewGuid().ToString()), new Claim("scp", grantedScope)],
            DateTime.UtcNow.AddHours(-1), expires, new SigningCredentials(key ?? BearerApiFactory.Key, SecurityAlgorithms.HmacSha256)));
    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task InvalidBearerRejected(string fault)
    {
        var key = fault == "signature" ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes("different-synthetic-signing-material-for-fixture")) : null;
        var token = Token(fault == "issuer" ? "https://untrusted.example" : BearerApiFactory.Issuer,
            fault == "audience" ? "other-api" : BearerApiFactory.Audience,
            fault == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5), key);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teams")).StatusCode);
    }
    [Fact] public async Task ValidTokenStillHasNoMembershipByDefault()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(BearerApiFactory.Issuer, BearerApiFactory.Audience, DateTime.UtcNow.AddMinutes(5)));
        var response = await client.GetAsync("/api/teams");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }
    [Fact] public async Task ValidTokenWithoutDelegatedEvaluationScopeForbidden()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(BearerApiFactory.Issuer, BearerApiFactory.Audience, DateTime.UtcNow.AddMinutes(5), grantedScope: "unrelated.scope"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/teams")).StatusCode);
    }
    public void Dispose() { client.Dispose(); factory.Dispose(); }
}
