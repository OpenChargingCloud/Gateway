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
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A gateway's certificate store over the wire: the four kinds of TLS and
    /// none of a vehicle's, what a TLS root is for - said at the upload,
    /// changed afterwards, taken back to every use - and a certificate copied
    /// into the directory, adopted at a reload and gone with its file.
    /// </summary>
    /// <remarks>
    /// Ported from the vehicle's CertificateUsagesTests, EV 6d6a0c6, where the
    /// store keeps all eleven kinds; what is the gateway's own is which kinds
    /// it keeps and what it says about the ones it does not.
    /// </remarks>
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

            var probe  = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port   = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, """{ "nts": { "enabled": false } }""");

            gateway    = new Gateway(
                             HTTPPort:          IPPort.Parse(port),
                             AccountsPath:      Path.Combine(directory, "accounts"),
                             ConfigFile:        new WWCPConfigFile(file),
                             CertificatesPath:  Path.Combine(directory, "certificates"),
                             LogToConsole:      false,
                             BridgeDebugLog:    false
                         );

            await gateway.Start();

            client     = new HttpClient {
                             BaseAddress  = new Uri($"http://127.0.0.1:{port}/"),
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


        #region (helpers) RootPem(Name) / Upload(Name) / IdentityUpload(Name) / Send(Method, Path, JSON)

        /// <summary>
        /// A self-signed certificate authority, as the text of a PEM file.
        /// </summary>
        private static String RootPem(String Name)
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request    = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return root.ExportCertificatePem();

        }

        /// <summary>
        /// A self-signed certificate authority, as the text of a PEM file
        /// base64-encoded - which is what an upload from the browser turns
        /// into.
        /// </summary>
        private static String Upload(String Name)

            => Convert.ToBase64String(Encoding.ASCII.GetBytes(RootPem(Name)));

        /// <summary>
        /// A certificate with its private key, as a PKCS#12 base64-encoded -
        /// what a gateway would present.
        /// </summary>
        private static String IdentityUpload(String Name)
        {

            using var key       = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request         = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            using var identity  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return Convert.ToBase64String(identity.Export(X509ContentType.Pkcs12));

        }

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


        #region ARootIsUploadedForTheUsesItIsFor()

        /// <summary>
        /// A TLS root for the time servers alone, and the store as the page
        /// reads it: the four kinds of TLS in their three groups, and nothing
        /// of ISO 15118's.
        /// </summary>
        [Test]
        public async Task ARootIsUploadedForTheUsesItIsFor()
        {

            var (created, entry) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                  new JProperty("kind",     "tlsRoot"),
                                                  new JProperty("content",  Upload("Our Clocks' Root")),
                                                  new JProperty("usages",   new JArray("nts"))
                                              ));

            var (_, store)       = await Send(HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {

                Assert.That(created,                                                        Is.EqualTo(HttpStatusCode.Created), entry.ToString());
                Assert.That(entry["usages"]!.Values<String>(),                              Is.EqualTo(new[] { "nts" }));

                Assert.That(store["usages"]!.Values<String>(),                              Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer");
                Assert.That(store["kinds"]!["tlsRoot"]!["hasUsages"]!.Value<Boolean>(),     Is.True);
                Assert.That(store["kinds"]!["tlsServer"]!["hasUsages"]!.Value<Boolean>(),   Is.True);
                Assert.That(store["kinds"]!["clientRoot"]!["hasUsages"]!.Value<Boolean>(),  Is.False);
                Assert.That(store["kinds"]!["tlsIdentity"]!["hasUsages"]!.Value<Boolean>(), Is.False,
                            "a gateway names no listener an identity could be told of, so a page offers it none");
                Assert.That(store["certificates"]!["tlsRoot"]![0]!["usages"]!.Values<String>(),  Is.EqualTo(new[] { "nts" }));

                Assert.That(store["trustAnchors"]!.Values<String>(),                        Is.EqualTo(new[] { "tlsRoot", "clientRoot" }));
                Assert.That(store["credentials"]!.Values<String>(),                         Is.EqualTo(new[] { "tlsIdentity" }));
                Assert.That(store["recognised"]!.Values<String>(),                          Is.EqualTo(new[] { "tlsServer" }),
                            "a server certificate is recognised, neither believed nor presented");

                Assert.That(((JObject) store["kinds"]!).Properties().Select(kind => kind.Name),
                            Is.EqualTo(new[] { "tlsRoot", "clientRoot", "tlsServer", "tlsIdentity" }),
                            "the kinds this store keeps, and none of a vehicle's");
                Assert.That(((JObject) store["certificates"]!).Properties().Select(kind => kind.Name),
                            Is.EqualTo(new[] { "tlsRoot", "clientRoot", "tlsServer", "tlsIdentity" }));

                Assert.That(store["keysAreUnencrypted"]!.Value<Boolean>(),                  Is.False, "a root carries no key");

            });

        }

        #endregion

        #region WhatARootIsForIsChangedAndTakenBackToEveryUse()

        [Test]
        public async Task WhatARootIsForIsChangedAndTakenBackToEveryUse()
        {

            var (_, entry)        = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                   new JProperty("kind",     "tlsRoot"),
                                                   new JProperty("content",  Upload("Our Resolvers' Root")),
                                                   new JProperty("usages",   new JArray("dns"))
                                               ));

            var path              = $"api/v1/certificates/{entry["id"]}";

            var (both,  forBoth)  = await Send(HttpMethod.Patch, path, new JObject(new JProperty("usages", new JArray("nts", "dns"))));
            var (label, relabel)  = await Send(HttpMethod.Patch, path, new JObject(new JProperty("label",  "Our Root")));
            var (every, forAll)   = await Send(HttpMethod.Patch, path, new JObject(new JProperty("usages", JValue.CreateNull())));

            Assert.Multiple(() => {
                Assert.That(both,                                      Is.EqualTo(HttpStatusCode.OK), forBoth.ToString());
                Assert.That(forBoth["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(label,                                     Is.EqualTo(HttpStatusCode.OK), relabel.ToString());
                Assert.That(relabel["label"]!.Value<String>(),         Is.EqualTo("Our Root"));
                Assert.That(relabel["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }), "a PATCH without them leaves them alone");
                Assert.That(every,                                     Is.EqualTo(HttpStatusCode.OK), forAll.ToString());
                Assert.That(forAll["usages"]!.Type,                    Is.EqualTo(JTokenType.Null),    "null is every use again");
                Assert.That(gateway!.Log.Recent(200, Tag: "security").Any(line => line.Message.Contains("is now for every use")),
                            Is.True,
                            "a change of what a root vouches for is a matter of security, and said as one");
            });

        }

        #endregion

        #region WhatIsNotAUsageIsRefusedWhereItIsTyped()

        [Test]
        public async Task WhatIsNotAUsageIsRefusedWhereItIsTyped()
        {

            var (unknown, said)       = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                       new JProperty("kind",     "tlsRoot"),
                                                       new JProperty("content",  Upload("Some Root")),
                                                       new JProperty("usages",   new JArray("ntp"))
                                                   ));

            var (onClient, clientSaid) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                       new JProperty("kind",     "clientRoot"),
                                                       new JProperty("content",  Upload("The Stations' Root")),
                                                       new JProperty("usages",   new JArray("nts"))
                                                   ));

            var (notAList, listSaid)  = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                       new JProperty("kind",     "tlsRoot"),
                                                       new JProperty("content",  Upload("Another Root")),
                                                       new JProperty("usages",   "dns")
                                                   ));

            // A TLS identity is told the listeners it is shown on, and a
            // gateway names none: the name servers are no place to show one.
            var (onIdentity, identitySaid) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                       new JProperty("kind",     "tlsIdentity"),
                                                       new JProperty("content",  IdentityUpload("This Gateway")),
                                                       new JProperty("usages",   new JArray("dns"))
                                                   ));

            var (_, store)            = await Send(HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(unknown,                       Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(said.ToString(),               Does.Contain("'ntp' is not a usage this gateway knows").And.Contain("dns, nts"));
                Assert.That(onClient,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(clientSaid.ToString(),         Does.Contain("only a TLS root and a server certificate"));
                Assert.That(onIdentity,                    Is.EqualTo(HttpStatusCode.BadRequest), identitySaid.ToString());
                Assert.That(identitySaid.ToString(),       Does.Contain("shown on every listener of this gateway"));
                Assert.That(notAList,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(listSaid.ToString(),           Does.Contain("has to be a list of usages"));
                Assert.That(store["certificates"]!.Values().SelectMany(kind => kind.Children()).Any(),
                            Is.False,
                            "nothing refused was half-imported");
            });

        }

        #endregion

        #region WhatAGatewayDoesNotKeepIsRefused()

        /// <summary>
        /// A vehicle's root is a kind of certificate the node knows and a
        /// gateway does not keep, and is refused by the store in its own words;
        /// a kind nobody knows is refused naming the four a gateway keeps - and
        /// neither leaves a directory behind.
        /// </summary>
        [Test]
        public async Task WhatAGatewayDoesNotKeepIsRefused()
        {

            var (vehicles, vehicleSaid) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "v2gRoot"),
                                                         new JProperty("content",  Upload("A V2G Root"))
                                                     ));

            var (nobodys, nobodySaid)   = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "root"),
                                                         new JProperty("content",  Upload("A Root Of Some Kind"))
                                                     ));

            Assert.Multiple(() => {
                Assert.That(vehicles,                     Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(vehicleSaid.ToString(),       Does.Contain("This gateway keeps no certificate of that kind"));
                Assert.That(nobodys,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(nobodySaid.ToString(),        Does.Contain("'kind' has to be one of tlsRoot, clientRoot, tlsServer, tlsIdentity."));
                Assert.That(Directory.Exists(Path.Combine(directory, "certificates", "roots", "v2g")),
                            Is.False,
                            "no directory for a kind the gateway does not keep");
                Assert.That(gateway!.Certificates.Entries,  Is.Empty);
            });

        }

        #endregion

        #region ACertificateCopiedInIsAdoptedAtAReloadAndGoesWithItsFile()

        /// <summary>
        /// The other way into the store: a PEM put into the directory by hand,
        /// adopted when the page reads the directory again - and deleted, file
        /// and all.
        /// </summary>
        [Test]
        public async Task ACertificateCopiedInIsAdoptedAtAReloadAndGoesWithItsFile()
        {

            var file              = Path.Combine(directory, "certificates", "roots", "tls", "copied-in.pem");

            File.WriteAllText(file, RootPem("Copied In"));

            var (reloaded, store) = await Send(HttpMethod.Post, "api/v1/certificates/reload", new JObject());

            var adopted           = store["certificates"]?["tlsRoot"]?.FirstOrDefault();
            var path              = $"api/v1/certificates/{adopted?["id"]}";

            var (found, one)      = await Send(HttpMethod.Get,    path);
            var (deleted, after)  = await Send(HttpMethod.Delete, path);
            var (gone, _)         = await Send(HttpMethod.Get,    path);

            Assert.Multiple(() => {
                Assert.That(reloaded,                                   Is.EqualTo(HttpStatusCode.OK), store.ToString());
                Assert.That(adopted?["label"]?.Value<String>(),         Is.EqualTo("Copied In"));
                Assert.That(adopted?["active"]?.Value<Boolean>(),       Is.True, "somebody put it there: it is switched on");
                Assert.That(found,                                      Is.EqualTo(HttpStatusCode.OK), one.ToString());
                Assert.That(deleted,                                    Is.EqualTo(HttpStatusCode.OK), after.ToString());
                Assert.That(after["certificates"]!["tlsRoot"]!.Any(),   Is.False);
                Assert.That(File.Exists(file),                          Is.False, "its file goes with it");
                Assert.That(gone,                                       Is.EqualTo(HttpStatusCode.NotFound));
            });

        }

        #endregion

    }

}
