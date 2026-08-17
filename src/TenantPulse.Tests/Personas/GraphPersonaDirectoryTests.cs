using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TenantPulse.Core.Configuration;
using TenantPulse.Engine.Graph;
using TenantPulse.Engine.Personas;

namespace TenantPulse.Tests.Personas;

/// <summary>
/// The cast list has to be the tenant's <em>people</em>. Two things a real CDX tenant throws at this
/// are covered here: room mailboxes that look like users, and Copilot licences that cannot be seen
/// where you would first look for them.
/// </summary>
public class GraphPersonaDirectoryTests
{
    private const string Reader = "cora@demo.onmicrosoft.com";
    private const string CopilotPlanId = "3f30311c-6b1e-48a4-ab79-725b469da960";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static IReadOnlyList<JsonElement> Users(params string[] users) =>
        [.. users.Select(Json)];

    private static string User(
        string id,
        string upn,
        string displayName,
        bool licensed = true,
        bool copilot = true) =>
        $$"""
          {
            "id": "{{id}}",
            "userPrincipalName": "{{upn}}",
            "displayName": "{{displayName}}",
            "mail": "{{upn}}",
            "accountEnabled": true,
            "userType": "Member",
            "jobTitle": "Analyst",
            "department": "Finance",
            "assignedLicenses": [{{(licensed ? """{ "skuId": "639dec6b-bb19-468b-871c-c5c441c4b0cb" }""" : "")}}],
            "assignedPlans": [
              { "service": "exchange", "capabilityStatus": "Enabled", "servicePlanId": "efb87545-963c-4e0d-99df-69c6916d9eb0" }
              {{(copilot ? $$""", { "service": "MicrosoftOffice", "capabilityStatus": "Enabled", "servicePlanId": "{{CopilotPlanId}}" }""" : "")}}
            ]
          }
          """;

    private const string SubscribedSkus =
        """
        {
          "skuPartNumber": "Microsoft_365_Copilot",
          "servicePlans": [
            { "servicePlanName": "M365_COPILOT_BUSINESS_CHAT", "servicePlanId": "3f30311c-6b1e-48a4-ab79-725b469da960" },
            { "servicePlanName": "EXCHANGE_S_ENTERPRISE", "servicePlanId": "efb87545-963c-4e0d-99df-69c6916d9eb0" }
          ]
        }
        """;

    private static GraphPersonaDirectory Create(IGraphClient graph) =>
        new(graph,
            new TenantPulseOptions { Tenant = { AllowedDomains = ["demo.onmicrosoft.com"] } },
            NullLogger<GraphPersonaDirectory>.Instance);

    private static GraphPersonaDirectory Create(IGraphClient graph, params string[] alwaysInclude) =>
        new(graph,
            new TenantPulseOptions
            {
                Tenant =
                {
                    AllowedDomains = ["demo.onmicrosoft.com"],
                    AlwaysIncludeUsers = [.. alwaysInclude]
                }
            },
            NullLogger<GraphPersonaDirectory>.Instance);

    private static IGraphClient Graph(IReadOnlyList<JsonElement> users, IReadOnlyList<JsonElement>? skus = null)
    {
        var graph = Substitute.For<IGraphClient>();

        graph.GetPagedAsync(Reader, Arg.Is<string>(p => p.StartsWith("users?")), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(users);

        graph.GetPagedAsync(Reader, "subscribedSkus", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(skus ?? Users(SubscribedSkus));

        return graph;
    }

    [Fact]
    public async Task Unlicensed_accounts_are_not_part_of_the_workforce()
    {
        // A conference room has a mailbox and an enabled account, but no licence — and does not send
        // email, edit documents or use Copilot.
        var graph = Graph(Users(
            User("1", "cora@demo.onmicrosoft.com", "Cora Thomas"),
            User("2", "adams@demo.onmicrosoft.com", "Conf Room Adams", licensed: false, copilot: false)));

        var personas = await Create(graph).LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Select(p => p.DisplayName).Should().BeEquivalentTo("Cora Thomas");
    }

    [Fact]
    public async Task Admin_and_service_accounts_are_excluded_by_default()
    {
        var graph = Graph(Users(
            User("1", "cora@demo.onmicrosoft.com", "Cora Thomas"),
            User("2", "admin@demo.onmicrosoft.com", "Tenant Admin"),
            User("3", "svc-backup@demo.onmicrosoft.com", "Backup Service")));

        var personas = await Create(graph).LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Where(p => !p.Excluded).Select(p => p.DisplayName)
            .Should().BeEquivalentTo("Cora Thomas");
    }

    [Fact]
    public async Task An_explicitly_included_admin_account_joins_the_workforce()
    {
        // Demoing as the admin account is a real case, and an empty mailbox is exactly the problem
        // this tool exists to solve.
        var graph = Graph(Users(
            User("1", "cora@demo.onmicrosoft.com", "Cora Thomas"),
            User("2", "admin@demo.onmicrosoft.com", "Tenant Admin"),
            User("3", "svc-backup@demo.onmicrosoft.com", "Backup Service")));

        var personas = await Create(graph, "ADMIN@demo.onmicrosoft.com")
            .LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Where(p => !p.Excluded).Select(p => p.DisplayName)
            .Should().BeEquivalentTo("Cora Thomas", "Tenant Admin");
    }

    [Fact]
    public async Task Explicit_inclusion_never_defeats_the_domain_allow_list()
    {
        // The domain list is a safety boundary; the admin heuristic is only tidiness.
        var graph = Graph(Users(
            User("1", "cora@demo.onmicrosoft.com", "Cora Thomas"),
            User("2", "admin@elsewhere.onmicrosoft.com", "Foreign Admin")));

        var personas = await Create(graph, "admin@elsewhere.onmicrosoft.com")
            .LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Where(p => !p.Excluded).Select(p => p.DisplayName)
            .Should().BeEquivalentTo("Cora Thomas");
    }

    [Fact]
    public async Task Copilot_licence_is_detected_from_the_service_plan_id()
    {
        // assignedPlans[].service never contains "Copilot" — not even for a fully licensed user — so
        // the service plan id has to be matched against the tenant's subscribed SKUs instead.
        var graph = Graph(Users(
            User("1", "cora@demo.onmicrosoft.com", "Cora Thomas"),
            User("2", "peyton@demo.onmicrosoft.com", "Peyton Davis", copilot: false)));

        var personas = await Create(graph).LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Single(p => p.DisplayName == "Cora Thomas").HasCopilotLicence.Should().BeTrue();
        personas.Single(p => p.DisplayName == "Peyton Davis").HasCopilotLicence.Should().BeFalse();
    }

    [Fact]
    public async Task Everyone_is_kept_when_licences_cannot_be_read()
    {
        // An older app registration without User.Read.All must degrade to the previous behaviour
        // rather than silently simulating nobody at all.
        var graph = Substitute.For<IGraphClient>();

        graph.GetPagedAsync(Reader, Arg.Is<string>(p => p.StartsWith("users?$select=id,userPrincipalName")), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Users(User("1", "cora@demo.onmicrosoft.com", "Cora Thomas", licensed: false, copilot: false)));

        graph.GetPagedAsync(Reader, Arg.Is<string>(p => p.StartsWith("users?$select=id,assignedLicenses")), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<JsonElement>>(_ => throw new GraphException(System.Net.HttpStatusCode.Forbidden, "users", "denied"));

        graph.GetPagedAsync(Reader, "subscribedSkus", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Users(SubscribedSkus));

        var personas = await Create(graph).LoadAsync(Reader, TestContext.Current.CancellationToken);

        personas.Select(p => p.DisplayName).Should().BeEquivalentTo("Cora Thomas");
        personas.Single().HasCopilotLicence.Should().BeFalse();
    }
}
