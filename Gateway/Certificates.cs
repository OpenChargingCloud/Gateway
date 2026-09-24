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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// What a certificate is called, what kind of key it carries, and its
    /// fingerprint - the three things a time server's test and the NTS page
    /// say about the certificates of a key exchange.
    /// </summary>
    /// <remarks>
    /// The vehicle has these on the entries of its certificate store. A
    /// gateway keeps no certificates of its own, so they stand here on their
    /// own, written the same way, so that a fingerprint read off the one can
    /// be compared with the other character for character.
    /// </remarks>
    public static class Certificates
    {

        #region ThumbprintOf(Certificate)

        /// <summary>
        /// A certificate's SHA-256 fingerprint, in lower-case hexadecimal.
        /// </summary>
        /// <remarks>
        /// SHA-256 rather than <see cref="X509Certificate2.Thumbprint"/>, which
        /// is SHA-1 and has no business identifying anything in 2026.
        /// </remarks>
        public static String ThumbprintOf(X509Certificate2 Certificate)

            => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

        #endregion

        #region CommonNameOf(Certificate)

        /// <summary>
        /// What to call a certificate: its common name, or its whole subject
        /// where it has none.
        /// </summary>
        public static String CommonNameOf(X509Certificate2 Certificate)
        {

            var common = Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            return common is { Length: > 0 }
                       ? common
                       : Certificate.Subject;

        }

        #endregion

        #region KeyAlgorithmOf(Certificate)

        /// <summary>
        /// What kind of key a certificate carries, written the way somebody
        /// comparing it against a specification would write it.
        /// </summary>
        /// <remarks>
        /// The curve is named rather than only the size, because what a
        /// specification asks for is a curve, and "521-bit EC" is one
        /// reasonable reading away from "P-521".
        /// </remarks>
        public static String KeyAlgorithmOf(X509Certificate2 Certificate)
        {

            using var ecdsa = Certificate.GetECDsaPublicKey();

            if (ecdsa is not null)
            {

                var curve = ecdsa.KeySize switch {
                                256  => "P-256",
                                384  => "P-384",
                                521  => "P-521",
                                _    => $"{ecdsa.KeySize}-bit"
                            };

                return $"ECDSA {curve}";

            }

            using var rsa = Certificate.GetRSAPublicKey();

            return rsa is not null
                       ? $"RSA {rsa.KeySize}-bit"
                       : Certificate.PublicKey.Oid.FriendlyName ?? Certificate.PublicKey.Oid.Value ?? "unknown";

        }

        #endregion

    }

}
