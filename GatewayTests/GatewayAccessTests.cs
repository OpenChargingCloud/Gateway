/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Gateway <https://github.com/OpenChargingCloud/Gateway>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// Who may do what on a gateway: the node's resources and no others, and
    /// its operator beside the node's viewer and administrators. A role from
    /// the configuration file is heard like every other on every node - the
    /// conformance suite of WWCP_Node_TestKit asks it of a gateway, see
    /// GatewayConformance.
    /// </summary>
    public class GatewayAccessTests
    {

        #region Data

        private const String  NoTimeServers  = """{ "nts": { "enabled": false } }""";

        private String   directory  = "";
        private Gateway? gateway;
        private Uri?     address;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-access-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task TearDown()
        {

            if (gateway is not null)
                await gateway.DisposeAsync();

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A temporary directory that outlives one test run is not worth
                // failing the run over.
            }

        }

        #endregion


        #region (helper) GatewayFrom()

        /// <summary>
        /// A gateway whose time client is switched off, on a free port of the
        /// loopback - made, and not yet started. Started through
        /// TestPorts.StartedOnFreshPorts, it is made again where its port was
        /// taken, and the gateway and the address are the last one's.
        /// </summary>
        private Gateway GatewayFrom()
        {

            var port   = TestPorts.Free();

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, NoTimeServers);

            gateway    = new Gateway(
                             HTTPPort:        IPPort.Parse(port),
                             AccountsPath:    Path.Combine(directory, "accounts"),
                             ConfigFile:      new WWCPConfigFile(file),
                             LogToConsole:    false,
                             BridgeDebugLog:  false
                         );

            address    = new Uri($"http://127.0.0.1:{port}/");

            return gateway;

        }

        #endregion

        #region (helper) SignedInAs(Name, Role)

        /// <summary>
        /// A client signed in with a password as an account of the given name,
        /// made for the purpose and put in the group of the given role - made
        /// the way the gateway makes its first one, so that it may sign in.
        /// </summary>
        private async Task<HttpClient> SignedInAs(String  Name,
                                                  String  Role)
        {

            var password = "correct-horse-battery-" + Guid.NewGuid().ToString("N")[..8];

            Assert.That(gateway!.ExtAPI.TryGetOrganization(Organization_Id.Parse("Gateway"), out var organization) &&
                        organization is Organization, Is.True, "the gateway's organization is not there");

            var account = await gateway.ExtAPI.CreateUser(
                                    User_Id.Parse(Name),
                                    I18NString.Create(Languages.en, Name),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    Password:                  password,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                                  Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(gateway.ExtAPI.TryGetUser(User_Id.Parse(Name), out var stored),           Is.True);
            Assert.That(gateway.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group),  Is.True, $"the gateway has no group '{Role}'");

            var joined = await gateway.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

            Assert.That(joined.IsSuccess, Is.True, $"'{Name}' could not be put in '{Role}'");

            var client = new HttpClient {
                             BaseAddress  = address,
                             Timeout      = TimeSpan.FromSeconds(30)
                         };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Name}:{password}"))
                                                         );

            return client;

        }

        #endregion

        #region (helper) Put(Client, Path, JSON)

        private static Task<HttpResponseMessage> Put(HttpClient  Client,
                                                     String      Path,
                                                     String      JSON)

            => Client.PutAsync(Path, new StringContent(JSON, Encoding.UTF8, "application/json"));

        #endregion

        #region (helper) GatewayAccessControl()

        /// <summary>
        /// The gateway's resources and roles, as a node told nothing else puts
        /// them together.
        /// </summary>
        private static AccessControl GatewayAccessControl()
        {

            Assert.That(AccessControl.TryCombine(GatewayAccess.Resources, GatewayAccess.Roles, null, null,
                                                 "gateway", out var access, out _, out var error),
                        Is.True, error);

            return access!;

        }

        #endregion


        #region AGatewayKnowsTheNodesResourcesAndItsThreeRoles()

        /// <summary>
        /// The node brings the viewer and the administrators, and the gateway
        /// its operator - and nothing of its own to be allowed, beside what
        /// every node has.
        /// </summary>
        [Test]
        public void AGatewayKnowsTheNodesResourcesAndItsThreeRoles()
        {

            var node = GatewayFrom();

            Assert.Multiple(() => {
                Assert.That(node.Roles,             Is.EqualTo(new[] { "viewer", "operator", WWCPNode.AdminRole }));
                Assert.That(node.Access.Resources,  Is.EqualTo(new[] { "configuration", "dns", "nts", "certificates", "ssh" }));
            });

        }

        #endregion

        #region EachRoleMayDoWhatItAlwaysMayDo(Role, Permission, Allowed)

        /// <summary>
        /// What each role could do before roles were data, permission by
        /// permission: the viewer looks, the operator also asks the name and
        /// time servers, and only the administrators repoint them - or change
        /// the certificates they are held to, or the SSH server, which
        /// everybody may look at.
        /// </summary>
        [TestCase("viewer",       "configuration:read",  true)]
        [TestCase("viewer",       "dns:read",            true)]
        [TestCase("viewer",       "nts:read",            true)]
        [TestCase("viewer",       "certificates:read",   true)]
        [TestCase("viewer",       "dns:edit",            false)]
        [TestCase("viewer",       "dns:run",             false)]
        [TestCase("viewer",       "nts:run",             false)]
        [TestCase("viewer",       "certificates:edit",   false)]
        [TestCase("viewer",       "ssh:read",            true)]
        [TestCase("viewer",       "ssh:edit",            false)]

        [TestCase("operator",     "configuration:read",  true)]
        [TestCase("operator",     "dns:read",            true)]
        [TestCase("operator",     "dns:run",             true)]
        [TestCase("operator",     "nts:run",             true)]
        [TestCase("operator",     "certificates:read",   true)]
        [TestCase("operator",     "dns:edit",            false)]
        [TestCase("operator",     "nts:edit",            false)]
        [TestCase("operator",     "certificates:edit",   false)]
        [TestCase("operator",     "ssh:read",            true)]
        [TestCase("operator",     "ssh:edit",            false)]

        [TestCase("systemadmin",  "dns:edit",            true)]
        [TestCase("systemadmin",  "nts:edit",            true)]
        [TestCase("systemadmin",  "nts:run",             true)]
        [TestCase("systemadmin",  "certificates:edit",   true)]
        [TestCase("systemadmin",  "ssh:edit",            true)]
        public void EachRoleMayDoWhatItAlwaysMayDo(String Role, String Permission, Boolean Allowed)
        {

            Assert.That(protocols.WWCP.Node.Web.Permission.TryParse(Permission, out var permission, out var error), Is.True, error);

            Assert.That(GatewayAccessControl().RoleNamed(Role)!.Allows(permission.Resource, permission.Operation), Is.EqualTo(Allowed));

        }

        #endregion


        #region AnOperatorMayLookAtTheDNSSettingsAndIsToldWhoMayChangeThem()

        /// <summary>
        /// Over the wire, as a browser signed in as an operator sees it: the
        /// page opens, the save is refused with the role to ask for, and what
        /// the browser is told it may do says the same beforehand.
        /// </summary>
        [Test]
        public async Task AnOperatorMayLookAtTheDNSSettingsAndIsToldWhoMayChangeThem()
        {

            await TestPorts.StartedOnFreshPorts(GatewayFrom);

            using var @operator  = await SignedInAs("operator1", "operator");

            var looked           = await @operator.GetAsync("api/v1/configuration/dns");
            var changed          = await Put(@operator, "api/v1/configuration/dns", "{}");
            var refusal          = await changed.Content.ReadAsStringAsync();
            var me               = JObject.Parse(await (await @operator.GetAsync("api/v1/auth/me")).Content.ReadAsStringAsync());
            var permissions      = me["permissions"]!.Values<String>().OfType<String>().ToArray();

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,               Is.EqualTo(HttpStatusCode.OK));
                Assert.That(changed.StatusCode,              Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                         Does.Contain("This needs the systemadmin role."));
                Assert.That(me["roles"]!.Values<String>(),   Is.EqualTo(new[] { "operator" }));
                Assert.That(permissions,                     Does.Contain("dns:run").And.Contain("nts:run").And.Contain("dns:read"));
                Assert.That(permissions,                     Does.Not.Contain("dns:edit").And.Not.Contain("nts:edit").And.Not.Contain("certificates:edit"));
                Assert.That(permissions.Any(permission => permission.StartsWith('*')),
                            Is.False,
                            "spelt out resource by resource, so that a page asking \"dns:read\" need not know what \"*\" is");
            });

        }

        #endregion

        #region AnOperatorMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()

        /// <summary>
        /// The store as a browser signed in as an operator sees it: it opens,
        /// and an upload, a reload and a deletion are each refused with the
        /// role to ask for - and nothing refused reached the store.
        /// </summary>
        [Test]
        public async Task AnOperatorMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()
        {

            await TestPorts.StartedOnFreshPorts(GatewayFrom);

            using var @operator  = await SignedInAs("operator2", "operator");

            var looked           = await @operator.GetAsync("api/v1/certificates");
            var uploaded         = await @operator.PostAsync("api/v1/certificates",
                                                             new StringContent("""{ "kind": "tlsRoot", "content": "AA==" }""",
                                                                               Encoding.UTF8, "application/json"));
            var reloaded         = await @operator.PostAsync("api/v1/certificates/reload",
                                                             new StringContent("{}", Encoding.UTF8, "application/json"));
            var deleted          = await @operator.DeleteAsync("api/v1/certificates/0123456789abcdef");
            var refusal          = await uploaded.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,             Is.EqualTo(HttpStatusCode.OK));
                Assert.That(uploaded.StatusCode,           Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                       Does.Contain("This needs the systemadmin role."));
                Assert.That(reloaded.StatusCode,           Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(deleted.StatusCode,            Is.EqualTo(HttpStatusCode.Forbidden),
                            "refused for who is asking, before it is asked whether there is such a certificate");
                Assert.That(gateway!.Certificates.Entries, Is.Empty);
            });

        }

        #endregion

        #region AnOperatorMayLookAtTheSSHServerAndIsToldWhoMayChangeIt()

        /// <summary>
        /// The SSH server as a browser signed in as an operator sees it: its
        /// page opens, and switching it off is refused with the role to ask
        /// for - the server where it was, and the file as it was.
        /// </summary>
        [Test]
        public async Task AnOperatorMayLookAtTheSSHServerAndIsToldWhoMayChangeIt()
        {

            await TestPorts.StartedOnFreshPorts(GatewayFrom);

            using var @operator  = await SignedInAs("operator3", "operator");

            var file             = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            var fileBefore       = File.ReadAllText(file);

            var looked           = await @operator.GetAsync("api/v1/configuration/ssh");
            var before           = JObject.Parse(await looked.Content.ReadAsStringAsync());
            var changed          = await Put(@operator, "api/v1/configuration/ssh", """{ "enabled": false }""");
            var refusal          = await changed.Content.ReadAsStringAsync();
            var after            = JObject.Parse(await (await @operator.GetAsync("api/v1/configuration/ssh")).Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,                  Is.EqualTo(HttpStatusCode.OK));
                Assert.That(changed.StatusCode,                 Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                            Does.Contain("This needs the systemadmin role."));
                Assert.That(after["enabled"]!.Value<Boolean>(), Is.EqualTo(before["enabled"]!.Value<Boolean>()), "the server was switched");
                Assert.That(after["port"]?.ToString(),          Is.EqualTo(before["port"]?.ToString()),          "the server was moved");
                Assert.That(File.ReadAllText(file),             Is.EqualTo(fileBefore),                          "the refused change reached the file");
            });

        }

        #endregion

    }

}
