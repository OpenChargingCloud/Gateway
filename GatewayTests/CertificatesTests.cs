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

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// Which of a gateway's kinds of certificate a page offers what it may be
    /// for, over the wire. What a root is for said at the upload, changed afterwards and
    /// taken back to every use, a usage made up kept and what is no usage or
    /// kind refused where it is typed, a certificate kept as several kinds,
    /// and a certificate copied into the directory and adopted when it is
    /// read again, is what every node does - the conformance suite of
    /// WWCP_Node_TestKit asks it of a gateway, see GatewayConformance.
    /// </summary>
    public class CertificatesTests
    {

        #region Data

        private String       directory  = "";
        private Gateway?     gateway;
        private HttpClient?  client;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public async Task Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "gateway-certificates-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, """{ "nts": { "enabled": false } }""");

            gateway    = await TestPorts.StartedOnFreshPorts(() => new Gateway(
                             HTTPPort:          IPPort.Parse(TestPorts.Free()),
                             AccountsPath:      Path.Combine(directory, "accounts"),
                             ConfigFile:        new WWCPConfigFile(file),
                             CertificatesPath:  Path.Combine(directory, "certificates"),
                             LogToConsole:      false,
                             BridgeDebugLog:    false
                         ));

            client     = new HttpClient {
                             BaseAddress  = new Uri($"http://127.0.0.1:{gateway.HTTPPort}/"),
                             Timeout      = TimeSpan.FromSeconds(30)
                         };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"root:{gateway.GeneratedPassword}"))
                                                         );

        }

        [TearDown]
        public async Task TearDown()
        {

            client?.Dispose();

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


        #region (helper) Send(Method, Path, JSON)

        private async Task<(HttpStatusCode Status, JObject JSON)> Send(HttpMethod  Method,
                                                                      String      Path,
                                                                      JObject?    JSON = null)
        {

            using var request   = new HttpRequestMessage(Method, Path);

            if (JSON is not null)
                request.Content = new StringContent(JSON.ToString(), Encoding.UTF8, "application/json");

            using var response  = await client!.SendAsync(request);
            var text            = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 ? JObject.Parse(text) : new JObject());

        }

        #endregion


        #region TheStoreSaysWhichOfAGatewaysKindsIsToldWhatItIsFor()

        /// <summary>
        /// The four kinds of TLS and none of a vehicle's, in their three
        /// groups - and of them the two a page offers what they may be for, a
        /// TLS root and a server certificate, offered the services a gateway
        /// has; not a client root, which nothing here uses yet, and not an
        /// identity, since a gateway names no listener one could be shown on.
        /// Any of them may still be marked with a usage made up.
        /// </summary>
        /// <remarks>
        /// What every node says of its store - that the answer is the store's
        /// word for each kind it keeps - is asked by the conformance suite of
        /// WWCP_Node_TestKit; this is what that word is for a gateway.
        /// </remarks>
        [Test]
        public async Task TheStoreSaysWhichOfAGatewaysKindsIsToldWhatItIsFor()
        {

            var (_, store) = await Send(HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {

                Assert.That(((JObject) store["kinds"]!).Properties().Select(kind => kind.Name),
                            Is.EqualTo(new[] { "tlsRoot", "clientRoot", "tlsServer", "tlsIdentity" }),
                            "the kinds this store keeps, and none of a vehicle's");

                Assert.That(store["usages"]!.Values<String>(),                               Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer");

                Assert.That(store["kinds"]!["tlsRoot"]!["usages"]!.Values<String>(),         Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer a root");
                Assert.That(store["kinds"]!["tlsServer"]!["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(store["kinds"]!["clientRoot"]!["usages"]!.Children().Any(),      Is.False,
                            "nothing here uses a client root yet, so a page offers it nothing");
                Assert.That(store["kinds"]!["tlsIdentity"]!["usages"]!.Children().Any(),     Is.False,
                            "a gateway names no listener an identity could be told of, so a page offers it nothing - not the services a root vouches for");

                Assert.That(store["trustAnchors"]!.Values<String>(),                         Is.EqualTo(new[] { "tlsRoot", "clientRoot" }));
                Assert.That(store["credentials"]!.Values<String>(),                          Is.EqualTo(new[] { "tlsIdentity" }));
                Assert.That(store["recognised"]!.Values<String>(),                           Is.EqualTo(new[] { "tlsServer" }),
                            "a server certificate is recognised, neither believed nor presented");

            });

        }

        #endregion

    }

}
