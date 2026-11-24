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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;

using org.GraphDefined.Vanaheimr.Hermod.PKI;

using BCx509 = Org.BouncyCastle.X509;

#endregion

namespace cloud.charging.open.Gateway.Tests
{

    /// <summary>
    /// A certificate authority for the duration of one test: a root, with an
    /// issuing CA below it where asked, that answers signing requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local controller's TestCA, cut down to what the gateway's tests
    /// ask of one: Bouncy Castle throughout, like the store it is tested
    /// against, and an elliptic curve P-256 key, which is what a fleet's
    /// authority normally is.
    /// </para>
    /// <para>
    /// Everything here is thrown away with the test, and none of these keys
    /// leaves the process - but .NET writes the intermediates of a chain it
    /// cannot complete into the Windows certificate stores as soon as a TLS
    /// context is built from it, and never takes them out. Disposing an
    /// authority takes its certificates out of those stores again, and
    /// <see cref="TestCAStoreSweep"/> those of every authority of the run.
    /// </para>
    /// </remarks>
    internal sealed class TestCA : IDisposable
    {

        #region Data

        /// <summary>
        /// The kind of key an authority gets.
        /// </summary>
        public const String Algorithm = "ecdsa-p256";

        /// <summary>
        /// The key of whichever certificate signs: the intermediate where there
        /// is one, the root otherwise.
        /// </summary>
        private readonly AsymmetricKeyParameter  issuerKey;

        private readonly BCx509.X509Certificate  issuer;

        private readonly KeyAlgorithm            algorithm;

        /// <summary>
        /// The thumbprints of every authority made in this run, for the sweep
        /// at its end.
        /// </summary>
        private static readonly ConcurrentDictionary<String, Byte> madeInThisRun = new();

        #endregion

        #region Properties

        /// <summary>
        /// The root of this authority, which is what a node is told to accept.
        /// </summary>
        public X509Certificate2  Certificate    { get; }

        /// <summary>
        /// The issuing certificate between the root and what it signs, when
        /// this authority was made with one.
        /// </summary>
        public X509Certificate2? Intermediate   { get; }

        #endregion

        #region Constructor(s)

        private TestCA(X509Certificate2        Certificate,
                       X509Certificate2?       Intermediate,
                       BCx509.X509Certificate  Issuer,
                       AsymmetricKeyParameter  IssuerKey,
                       KeyAlgorithm            Algorithm)
        {

            this.Certificate   = Certificate;
            this.Intermediate  = Intermediate;
            this.issuer        = Issuer;
            this.issuerKey     = IssuerKey;
            this.algorithm     = Algorithm;

            madeInThisRun.TryAdd(Certificate.Thumbprint, 0);

            if (Intermediate is not null)
                madeInThisRun.TryAdd(Intermediate.Thumbprint, 0);

        }

        #endregion


        #region (static) Create(Name, WithIntermediate = false)

        /// <summary>
        /// A new authority, valid from 2000 to 2100: an authority cannot sign a
        /// certificate reaching outside its own validity.
        /// </summary>
        public static TestCA Create(String   Name,
                                    Boolean  WithIntermediate = false)
        {

            var algorithm  = KeyAlgorithm.Find(Algorithm)
                                 ?? throw new InvalidOperationException($"'{Algorithm}' is not a kind of key this library makes.");

            var notBefore  = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var notAfter   = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var rootPair   = algorithm.Generate();
            var root       = Authority($"CN={Name}", rootPair.Public, rootPair, notBefore, notAfter, null, algorithm);

            if (!WithIntermediate)
                return new TestCA(ToDotNet(root), null, root, rootPair.Private, algorithm);

            var intermediatePair  = algorithm.Generate();
            var intermediate      = Authority($"CN={Name} Issuing CA", intermediatePair.Public, rootPair, notBefore, notAfter, root.SubjectDN, algorithm);

            return new TestCA(ToDotNet(root), ToDotNet(intermediate), intermediate, intermediatePair.Private, algorithm);

        }

        /// <summary>
        /// One certificate authority certificate, self-signed or not.
        /// </summary>
        private static BCx509.X509Certificate Authority(String                   Subject,
                                                        AsymmetricKeyParameter   PublicKey,
                                                        AsymmetricCipherKeyPair  Signer,
                                                        DateTime                 NotBefore,
                                                        DateTime                 NotAfter,
                                                        X509Name?                Issuer,
                                                        KeyAlgorithm             Algorithm)
        {

            var generator = new X509V3CertificateGenerator();

            generator.SetSerialNumber(Serial());
            generator.SetIssuerDN    (Issuer ?? new X509Name(Subject));
            generator.SetSubjectDN   (new X509Name(Subject));
            generator.SetNotBefore   (NotBefore);
            generator.SetNotAfter    (NotAfter);
            generator.SetPublicKey   (PublicKey);

            generator.AddExtension(X509Extensions.BasicConstraints, true,  new BasicConstraints(true));
            generator.AddExtension(X509Extensions.KeyUsage,         true,  new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));

            return generator.Generate(new Asn1SignatureFactory(Algorithm.SignatureAlgorithm, Signer.Private));

        }

        #endregion

        #region Sign(CSR, NotBefore, NotAfter, ClientAuthentication = false)

        /// <summary>
        /// Answer a signing request, over the given period, with the extensions
        /// it asked for - or, where told, for client authentication instead of
        /// what it asked to be used for.
        /// </summary>
        public X509Certificate2 Sign(String          CSR,
                                     DateTimeOffset  NotBefore,
                                     DateTimeOffset  NotAfter,
                                     Boolean         ClientAuthentication = false)
        {

            var request   = (Pkcs10CertificationRequest) new PemReader(new StringReader(CSR)).ReadObject();
            var info      = request.GetCertificationRequestInfo();

            var generator = new X509V3CertificateGenerator();

            generator.SetSerialNumber(Serial());
            generator.SetIssuerDN    (issuer.SubjectDN);
            generator.SetSubjectDN   (info.Subject);
            generator.SetNotBefore   (NotBefore.UtcDateTime);
            generator.SetNotAfter    (NotAfter. UtcDateTime);
            generator.SetPublicKey   (request.GetPublicKey());

            var requested = request.GetRequestedExtensions();

            if (requested is not null)
                foreach (DerObjectIdentifier oid in requested.ExtensionOids)
                {

                    if (ClientAuthentication && oid.Equals(X509Extensions.ExtendedKeyUsage))
                        continue;

                    var extension = requested.GetExtension(oid);

                    generator.AddExtension(oid, extension.IsCritical, extension.GetParsedValue());

                }

            if (ClientAuthentication)
                generator.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                                       new ExtendedKeyUsage(KeyPurposeID.id_kp_clientAuth));

            return ToDotNet(generator.Generate(new Asn1SignatureFactory(algorithm.SignatureAlgorithm, issuerKey)));

        }

        #endregion

        #region ToPEM(Certificates) / ChainPEM(Certificate)

        /// <summary>
        /// Certificates the way they arrive from an authority: one PEM block
        /// after another in one file.
        /// </summary>
        public static String ToPEM(params X509Certificate2[] Certificates)

            => String.Join(
                   Environment.NewLine,
                   Certificates.Select(certificate => PemEncoding.WriteString("CERTIFICATE", certificate.RawData))
               ) + Environment.NewLine;

        /// <summary>
        /// A signed certificate together with the intermediate it needs, which
        /// is what an authority that has one sends back.
        /// </summary>
        public String ChainPEM(X509Certificate2 Certificate)

            => Intermediate is null
                   ? ToPEM(Certificate)
                   : ToPEM(Certificate, Intermediate);

        #endregion

        #region (private static) ToDotNet(Certificate) / Serial()

        private static X509Certificate2 ToDotNet(BCx509.X509Certificate Certificate)
            => X509CertificateLoader.LoadCertificate(Certificate.GetEncoded());

        private static BigInteger Serial()
            => new (1, RandomNumberGenerator.GetBytes(16));

        #endregion

        #region (static) RemoveFromWindowsStore(Thumbprints) / RemoveAllFromWindowsStore()

        /// <summary>
        /// Take these certificates out of the Windows stores of intermediate
        /// authorities, where .NET may have put them on its own: both stores,
        /// each on its own, since which one .NET chose depends on whether the
        /// run has administrator rights. A store that cannot be opened for
        /// writing holds nothing of ours.
        /// </summary>
        private static void RemoveFromWindowsStore(IEnumerable<String> Thumbprints)
        {

            if (!OperatingSystem.IsWindows())
                return;

            var thumbprints = Thumbprints.ToArray();

            if (thumbprints.Length == 0)
                return;

            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                try
                {

                    using var store = new X509Store(StoreName.CertificateAuthority, location);
                    store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);

                    foreach (var thumbprint in thumbprints)
                        foreach (var found in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                        {
                            store.Remove(found);
                            found.Dispose();
                        }

                }
                catch (CryptographicException)
                { }
            }

        }

        /// <summary>
        /// Take every authority of this run out of the Windows stores again,
        /// including those of a test whose teardown never ran.
        /// </summary>
        public static void RemoveAllFromWindowsStore()
            => RemoveFromWindowsStore(madeInThisRun.Keys);

        #endregion

        #region Dispose()

        public void Dispose()
        {

            RemoveFromWindowsStore(Intermediate is null
                                       ? [ Certificate.Thumbprint ]
                                       : [ Certificate.Thumbprint, Intermediate.Thumbprint ]);

            Certificate.  Dispose();
            Intermediate?.Dispose();

        }

        #endregion

    }

}
