using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;
using Factory.Plant.Service;
using Factory.Plant.Service.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using static Factory.Tests.PlantApiTests;

namespace Factory.Tests;

public sealed class WebsiteTests
{
    [Fact]
    public async Task ProductionLineFormsPersistDefinitionsAndVersionedAssignmentsWithAntiforgery()
    {
        using var app = new WebsiteTestApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Line plant");
        var enrollment = ProductionLineTests.SeedMember(app, plant.Id);
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser);
        using var createPage = await browser.GetAsync("/production-lines/create");
        Assert.Equal(HttpStatusCode.OK, createPage.StatusCode);
        var html = await createPage.Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["Name"] = "ProductZero",
            ["SourceRepository"] = "chieftn/productionOutput1",
            ["BaseBranch"] = "main",
            ["WorkloadPath"] = "productionLine",
            ["SourceConnection"] = "github-source",
            ["TaskRepository"] = "chieftn/productionOutput1 ",
            ["TaskConnection"] = "github-tasks",
            ["RequiredLabels"] = "approved",
            ["MaximumIterations"] = "3",
            ["TimeoutMinutes"] = "30",
            ["OperationKey"] = Hidden(html, "OperationKey"),
            ["Revision"] = Hidden(html, "Revision")
        };
        using var noToken = await browser.PostAsync("/production-lines", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Empty(app.Store.All<ProductionLineRecord>());
        form["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        using var created = await browser.PostAsync("/production-lines", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var line = Assert.Single(app.Store.All<ProductionLineRecord>()).Line;
        Assert.Equal("chieftn/productionOutput1", line.Definition.Tasks.Repository);
        using var definitionPage = await browser.GetAsync($"/production-lines/{line.Id}");
        Assert.Equal(HttpStatusCode.OK, definitionPage.StatusCode);
        html = await definitionPage.Content.ReadAsStringAsync();
        form["OperationKey"] = Hidden(html, "OperationKey");
        form["Revision"] = Hidden(html, "Revision");
        form["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        form["Name"] = "Revised ProductZero";
        using var edited = await browser.PostAsync($"/production-lines/{line.Id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        Assert.Equal("Revised ProductZero", Assert.Single(app.Store.All<ProductionLineRecord>()).Line.Definition.Name);
        var editedRevision = Assert.Single(app.Store.All<ProductionLineRecord>()).Line.Revision;
        form["Revision"] = "";
        form["OperationKey"] = Guid.NewGuid().ToString("D");
        form["Name"] = "Must not overwrite without a revision";
        using var missingRevision = await browser.PostAsync($"/production-lines/{line.Id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        Assert.Contains("A revision is required when updating a line.", await missingRevision.Content.ReadAsStringAsync());
        Assert.Equal(editedRevision, Assert.Single(app.Store.All<ProductionLineRecord>()).Line.Revision);
        using var plantPage = await browser.GetAsync($"/plants/{plant.Id}");
        html = await plantPage.Content.ReadAsStringAsync();
        Assert.Contains($"/plants/{plant.Id}/production-line", html);
        Assert.DoesNotContain($"/workbenches/{enrollment.WorkbenchId}/production-line", html);
        var route = $"/plants/{plant.Id}/production-line";
        using var assignmentPage = await browser.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, assignmentPage.StatusCode);
        html = await assignmentPage.Content.ReadAsStringAsync();
        var assignment = new Dictionary<string, string>
        {
            ["ExpectedVersion"] = "0",
            ["ProductionLineId"] = $" {line.Id} ",
            ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
        };
        using var assigned = await browser.PostAsync(route, new FormUrlEncodedContent(assignment));
        Assert.Equal(HttpStatusCode.Redirect, assigned.StatusCode);
        Assert.Equal(line.Id, Assert.Single(app.Store.All<ProductionLineBindingRecord>()).Binding.ProductionLineId);
        using var inheritedPage = await browser.GetAsync($"/plants/{plant.Id}/workbenches/{enrollment.WorkbenchId}");
        html = await inheritedPage.Content.ReadAsStringAsync();
        Assert.Contains("Configuration is inherited from the plant", html);
        Assert.Contains(line.Id, html);
        Assert.Contains("Configuration has not been acknowledged by the workbench.", html);
        Assert.DoesNotContain("Manage production line", html);
        assignment["ExpectedVersion"] = "1";
        assignment["ProductionLineId"] = "";
        using var revoked = await browser.PostAsync(route, new FormUrlEncodedContent(assignment));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Null(Assert.Single(app.Store.All<ProductionLineBindingRecord>()).Binding.ProductionLineId);
        using var admin = app.Client(app.Token(ApiTestApplication.OwnerId, ["Factory.User", ApiCaller.EnrollmentAdministratorRole]));
        using var unassigned = await admin.PutAsJsonAsync($"/v1/enrollments/{enrollment.Id}/assignment",
            new { schemaVersion = "1", expectedVersion = 1 });
        unassigned.EnsureSuccessStatusCode();
        app.Store.Seed(new ConfigurationObservationRecord
        {
            Id = "configuration-observation",
            ScopeId = enrollment.ScopeId,
            EnrollmentId = enrollment.Id,
            RecordedAt = app.Clock.Now,
            Observation = new()
            {
                SchemaVersion = "1",
                AssignmentVersion = 2,
                BindingVersion = 0,
                State = "unconfigured",
                Reason = "Revocation acknowledged; no execution was started."
            }
        });
        using var revocationPage = await browser.GetAsync($"/plants/{plant.Id}/workbenches/{enrollment.WorkbenchId}");
        Assert.Equal(HttpStatusCode.OK, revocationPage.StatusCode);
        Assert.Contains("Desired configuration acknowledged.", await revocationPage.Content.ReadAsStringAsync());
        using var apiWithCookie = await browser.GetAsync("/v1/production-lines");
        await ProblemAsync(apiWithCookie, 401, "unauthenticated");
    }

    [Fact]
    public async Task EnrollmentInventoryAssignsAndUnassignsThroughProtectedFormsWithoutReplacingIdentity()
    {
        using var app = new WebsiteTestApplication { EnableEnrollment = true };
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Assignment plant");
        var id = Guid.NewGuid().ToString("D");
        var installation = Guid.NewGuid().ToString("D");
        var enrolled = new EnrollmentRecord
        {
            Id = id,
            ScopeId = Records.EnrollmentScope(id),
            State = "active",
            WorkbenchId = $"wb-{Guid.Parse(installation):N}",
            Draft = new EnrollmentDraft
            {
                SchemaVersion = "1",
                OperationId = id,
                InstallationId = installation,
                MachineName = "Unassigned fixture",
                PublicCertificate = "AQID"
            },
            Requester = new(ApiTestApplication.Tenant, ApiTestApplication.OwnerId),
            RequesterClientId = ApiTestApplication.Audience,
            Digest = "fixture",
            UpdatedAt = app.Clock.Now,
            AuthorizedAt = app.Clock.Now,
            ClientId = ApiTestApplication.DaemonClientId,
            ServicePrincipalId = ApiTestApplication.DaemonId
        };
        app.Store.Seed(enrolled);
        using var admin = app.Browser();
        using var adminSignIn = await app.SignInAsync(admin, roles:
            ["Factory.User", "Factory.PlantCreator", ApiCaller.EnrollmentAdministratorRole]);
        using var inventory = await admin.GetAsync("/enrollments");
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
        var html = await inventory.Content.ReadAsStringAsync();
        Assert.Contains("Unassigned fixture", html);
        var token = Hidden(html, "__RequestVerificationToken");
        var route = $"/enrollments/{id}/assignment";
        using var missingToken = await admin.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["plantId"] = plant.Id, ["expectedVersion"] = "0" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        var form = new Dictionary<string, string>
        {
            ["plantId"] = plant.Id,
            ["expectedVersion"] = "0",
            ["__RequestVerificationToken"] = token
        };
        using var assigned = await admin.PostAsync(route, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, assigned.StatusCode);
        Assert.Equal(plant.Id, Assert.Single(app.Store.All<EnrollmentRecord>()).AssignedPlantId);
        form["plantId"] = "";
        form["expectedVersion"] = "1";
        using var unassigned = await admin.PostAsync(route, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, unassigned.StatusCode);
        var current = Assert.Single(app.Store.All<EnrollmentRecord>());
        Assert.Null(current.AssignedPlantId);
        Assert.Equal(enrolled.ClientId, current.ClientId);
        Assert.Equal(enrolled.ServicePrincipalId, current.ServicePrincipalId);
        using var ordinary = app.Browser();
        using var ordinarySignIn = await app.SignInAsync(ordinary);
        using var denied = await ordinary.GetAsync("/enrollments");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/account/access-denied", denied.Headers.Location!.OriginalString);
        using var retired = await admin.PostAsync($"/enrollments/{id}/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "true", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, retired.StatusCode);
        Assert.Equal("revoking", Assert.Single(app.Store.All<EnrollmentRecord>()).State);
    }

    [Fact]
    public async Task BrowserSignInUsesPkceAndACanonicalCallbackWhileApiRemainsBearerOnly()
    {
        using var app = new WebsiteTestApplication();
        using var browser = app.Browser();
        using (var anonymous = await browser.GetAsync("/plants"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/account/signin", anonymous.Headers.Location!.OriginalString);
        }
        using (var spoofed = new HttpRequestMessage(HttpMethod.Get, "/plants"))
        {
            spoofed.Headers.Host = "attacker.example.invalid";
            using var response = await browser.SendAsync(spoofed);
            Assert.False(response.Headers.Location!.IsAbsoluteUri);
            Assert.StartsWith("/account/signin", response.Headers.Location.OriginalString);
        }
        using (var challenge = await browser.GetAsync("/account/signin?returnUrl=%2Fplants"))
        {
            var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
            Assert.Equal("code", query["response_type"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"]);
            Assert.DoesNotContain("token", query["response_type"].ToString());
        }
        using (var callback = await app.SignInAsync(browser))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.True(callback.Headers.Location!.OriginalString == "/plants", app.AuthenticationFailure?.ToString());
            var cookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith("__Host-Factory.Session=", StringComparison.Ordinal));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        }
        using (var page = await browser.GetAsync("/plants"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        }
        using (var api = await browser.GetAsync("/v1/plants"))
            await ProblemAsync(api, 401, "unauthenticated");
        using (var human = app.Human())
        using (var page = await human.GetAsync("/plants"))
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        using (var external = await browser.GetAsync("/account/signin?returnUrl=https%3A%2F%2Fexample.invalid"))
            Assert.Equal(HttpStatusCode.BadRequest, external.StatusCode);
        using (var spoofed = new HttpRequestMessage(HttpMethod.Get, "/account/signin"))
        {
            spoofed.Headers.Host = "attacker.example.invalid";
            using var response = await browser.SendAsync(spoofed);
            Assert.Equal("https://localhost/signin-oidc", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"]);
        }
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("tenant")]
    [InlineData("nonce")]
    [InlineData("missing-oid")]
    [InlineData("duplicate-oid")]
    [InlineData("no-role")]
    public async Task InvalidProviderResponsesCannotCreateBrowserSessions(string invalid)
    {
        using var app = new WebsiteTestApplication
        {
            WrongAudience = invalid == "audience",
            WrongTenant = invalid == "tenant",
            WrongNonce = invalid == "nonce",
            MissingObjectId = invalid == "missing-oid",
            DuplicateObjectId = invalid == "duplicate-oid"
        };
        using var browser = app.Browser();
        using var callback = await app.SignInAsync(browser, roles: invalid == "no-role" ? [] : null);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/account/signin-error", callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain(callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-Factory.Session=", StringComparison.Ordinal));
        using var page = await browser.GetAsync("/plants");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
    }

    [Fact]
    public async Task PlantFormsRequireAntiforgeryAssignTheCookieOwnerAndKeepRetriesIdempotent()
    {
        using var app = new WebsiteTestApplication();
        using var browser = app.Browser();
        using (var signedIn = await app.SignInAsync(browser))
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var form = await CreationFormAsync(browser);
        using (var noToken = await browser.PostAsync("/plants", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Rejected",
            ["OperationKey"] = form["OperationKey"]
        })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.Equal("text/html", noToken.Content.Headers.ContentType?.MediaType);
        }
        Assert.Empty(app.Store.All<PlantRecord>());
        form["Name"] = "<script>alert('plant')</script>";
        form["Owner.ObjectId"] = ApiTestApplication.ReaderId;
        using var created = await browser.PostAsync("/plants", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var plant = Assert.Single(app.Store.All<PlantRecord>()).Plant;
        Assert.Equal(ApiTestApplication.OwnerId, plant.Owner.ObjectId);
        Assert.Equal($"/plants/{plant.Id}", created.Headers.Location!.OriginalString);
        using (var replay = await browser.PostAsync("/plants", new FormUrlEncodedContent(form)))
            Assert.Equal(created.Headers.Location, replay.Headers.Location);
        Assert.Single(app.Store.All<PlantRecord>());
        using var detail = await browser.GetAsync(created.Headers.Location);
        var html = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains($"/plants/{plant.Id}/workbenches", html);
    }

    [Fact]
    public async Task FailedCreationKeepsTheOriginalInputAndOperationKeyForRetry()
    {
        using var app = new WebsiteTestApplication();
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser);
        var form = await CreationFormAsync(browser);
        var key = form["OperationKey"];
        form["Name"] = " ";
        using (var invalid = await browser.PostAsync("/plants", new FormUrlEncodedContent(form)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("non-whitespace", await invalid.Content.ReadAsStringAsync());
        }
        form["Name"] = "Retry plant";
        app.Store.FailWrites = true;
        using (var unavailable = await browser.PostAsync("/plants", new FormUrlEncodedContent(form)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            var html = await unavailable.Content.ReadAsStringAsync();
            Assert.Equal(key, Hidden(html, "OperationKey"));
            Assert.Contains("Retry plant", html);
            Assert.NotNull(unavailable.Headers.RetryAfter);
            form["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        }
        app.Store.FailWrites = false;
        using var retry = await browser.PostAsync("/plants", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Single(app.Store.All<PlantRecord>());
    }

    [Fact]
    public async Task CreatorRoleIsRequiredAndDifferentUsersCannotReuseAnAntiforgeryToken()
    {
        using var app = new WebsiteTestApplication();
        using var creator = app.Browser();
        using var reader = app.Browser();
        using var creatorSignedIn = await app.SignInAsync(creator);
        using var readerSignedIn = await app.SignInAsync(reader, ApiTestApplication.ReaderId, creator: false);
        var form = await CreationFormAsync(creator);
        using (var denied = await reader.GetAsync("/plants/create"))
        {
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/account/access-denied", denied.Headers.Location!.OriginalString);
        }
        using (var denied = await reader.PostAsync("/plants", new FormUrlEncodedContent(form)))
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var anotherCreator = app.Browser();
        using var anotherSignedIn = await app.SignInAsync(anotherCreator, ApiTestApplication.ReaderId);
        var ownForm = await CreationFormAsync(anotherCreator);
        ownForm["__RequestVerificationToken"] = form["__RequestVerificationToken"];
        using var wrongUser = await anotherCreator.PostAsync("/plants", new FormUrlEncodedContent(ownForm));
        Assert.Equal(HttpStatusCode.BadRequest, wrongUser.StatusCode);
        Assert.Empty(app.Store.All<PlantRecord>());
    }

    [Fact]
    public async Task ScopedWorkbenchViewsFilterSiblingsAndRecheckAccessOnEveryRequest()
    {
        using var app = new WebsiteTestApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "Visible plant");
        app.Store.Seed(WorkbenchApiTests.Workbench(app, plant.Id, "box-a"));
        app.Store.Seed(WorkbenchApiTests.Workbench(app, plant.Id, "box-b"));
        var grant = new AccessRecord
        {
            Id = "web-reader",
            PlantId = plant.Id,
            ScopeId = Records.PlantScope(plant.Id),
            Principal = new(ApiTestApplication.Tenant, ApiTestApplication.ReaderId),
            Role = "Plant.Reader",
            WorkbenchId = "box-a"
        };
        app.Store.Seed(grant);
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser, ApiTestApplication.ReaderId, creator: false);
        using (var page = await browser.GetAsync($"/plants/{plant.Id}/workbenches"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Approved box-a", html);
            Assert.DoesNotContain("Approved box-b", html);
        }
        using (var allowed = await browser.GetAsync($"/plants/{plant.Id}/workbenches/box-a"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            var html = await allowed.Content.ReadAsStringAsync();
            Assert.Contains("unknown", html);
            Assert.Contains("Not reported", html);
        }
        using (var sibling = await browser.GetAsync($"/plants/{plant.Id}/workbenches/box-b"))
            Assert.Equal(HttpStatusCode.Forbidden, sibling.StatusCode);
        using (var parent = await browser.GetAsync($"/plants/{plant.Id}"))
            Assert.Equal(HttpStatusCode.Forbidden, parent.StatusCode);
        app.Store.Seed(grant with { Role = "Plant.EnrollmentRequester" });
        using var revoked = await browser.GetAsync($"/plants/{plant.Id}/workbenches/box-a");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal("text/html", revoked.Content.Headers.ContentType?.MediaType);
        Assert.True(revoked.Headers.CacheControl?.NoStore);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(revoked.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task SignOutRequiresAPostWithAntiforgeryAndClearsTheBrowserSession()
    {
        using var app = new WebsiteTestApplication();
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser);
        var form = await CreationFormAsync(browser);
        using (var get = await browser.GetAsync("/account/signout"))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var noToken = await browser.PostAsync("/account/signout", new FormUrlEncodedContent([])))
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var signout = await browser.PostAsync("/account/signout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = form["__RequestVerificationToken"]
        }));
        Assert.Equal(HttpStatusCode.Redirect, signout.StatusCode);
        Assert.Equal("https://localhost/signout-callback-oidc",
            QueryHelpers.ParseQuery(signout.Headers.Location!.Query)["post_logout_redirect_uri"]);
        using var page = await browser.GetAsync("/plants");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/account/signin", page.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task BrowserSessionsExpireAfterOneHourWithoutSlidingRenewal()
    {
        using var app = new WebsiteTestApplication();
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser);
        app.Clock.Now += TimeSpan.FromMinutes(40);
        using (var active = await browser.GetAsync("/plants"))
        {
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
            Assert.DoesNotContain(active.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith("__Host-Factory.Session=", StringComparison.Ordinal));
        }
        app.Clock.Now += TimeSpan.FromMinutes(21);
        using var expired = await browser.GetAsync("/plants");
        Assert.Equal(HttpStatusCode.Redirect, expired.StatusCode);
        Assert.Contains("/account/signin", expired.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task RazorPaginationPreservesPageSizeAndWorkbenchReportsRemainSeparate()
    {
        using var app = new WebsiteTestApplication();
        using var owner = app.Human();
        var (plant, _) = await CreateAsync(owner, "First");
        await CreateAsync(owner, "Second");
        using var browser = app.Browser();
        using var signedIn = await app.SignInAsync(browser);
        using (var page = await browser.GetAsync("/plants?pageSize=1"))
        {
            var html = await page.Content.ReadAsStringAsync();
            var next = Regex.Match(html, "href=\"([^\"]*continuationToken[^\"]*)\"",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.True(next.Success);
            var link = WebUtility.HtmlDecode(next.Groups[1].Value);
            Assert.Contains("pageSize=1", link);
            using var second = await browser.GetAsync(link);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.DoesNotContain("Next page", await second.Content.ReadAsStringAsync());
        }
        app.Store.Seed(WorkbenchApiTests.Workbench(app, plant.Id, "box-a"));
        using var daemon = app.Daemon();
        var api = $"/v1/plants/{plant.Id}/workbenches/box-a";
        using (var registration = await SendAsync(daemon, HttpMethod.Put, api + "/registration", new WorkbenchRegistration
        {
            SchemaVersion = "1",
            MachineName = "<img src=x onerror=alert(1)>",
            HostVersion = "host-test",
            WorkerVersion = "worker-test",
            ProtocolVersion = "1",
            AssignmentSchemaVersions = ["1"],
            Capabilities = ["workspace"]
        }, Key()))
            Assert.Equal(HttpStatusCode.NoContent, registration.StatusCode);
        using (var presence = await SendAsync(daemon, HttpMethod.Put, api + "/presence",
            new WorkbenchPresence { SchemaVersion = "1", Status = "idle" }, Key()))
            Assert.Equal(HttpStatusCode.NoContent, presence.StatusCode);
        app.Clock.Now += TimeSpan.FromSeconds(91);
        using var detail = await browser.GetAsync($"/plants/{plant.Id}/workbenches/box-a");
        var body = await detail.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("<dd>active</dd>", body);
        Assert.Contains("<dd>stale</dd>", body);
        Assert.Contains("<dd>idle</dd>", body);
        Assert.Contains("host-test", body);
        Assert.Contains("&lt;img", body);
        Assert.DoesNotContain("<img", body);
    }

    private static async Task<Dictionary<string, string>> CreationFormAsync(HttpClient browser)
    {
        using var page = await browser.GetAsync("/plants/create");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/plants\"", html);
        return new()
        {
            ["Name"] = "Web plant",
            ["OperationKey"] = Hidden(html, "OperationKey"),
            ["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken")
        };
    }

    private static string Hidden(string html, string name)
    {
        var match = Regex.Match(html, $"""<input\b[^>]*\bname="{Regex.Escape(name)}"[^>]*\bvalue="([^"]*)"[^>]*>""",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, $"Missing form field: {name}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
