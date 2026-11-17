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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.Gateway.Configuration
{

    /// <summary>
    /// The "ocpp" section of the configuration file: who this gateway says it
    /// is when it speaks OCPP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section a local controller has, field for field and with the same
    /// limits, so that one file reads the same on either: what differs is what
    /// a gateway calls itself when nothing says otherwise.
    /// </para>
    /// <para>
    /// Null means "the file does not say": what is missing keeps whatever the
    /// gateway was given at construction, and what it was given falls back to
    /// the defaults here.
    /// </para>
    /// <para>
    /// Read once, at the start, and not changeable while running. An
    /// identification is what a CSMS knows this gateway by; changing it under a
    /// live connection would not rename the gateway, it would make it a second
    /// one that nobody is talking to.
    /// </para>
    /// </remarks>
    /// <param name="NodeId">The networking node identification, e.g. "gw001".</param>
    /// <param name="VendorName">Who made this gateway.</param>
    /// <param name="Model">What model it is.</param>
    /// <param name="SerialNumber">Its serial number, or null when it has none to give.</param>
    /// <param name="SoftwareVersion">The version it reports, or null to report the version of this assembly.</param>
    public sealed record OCPPConfiguration(String?  NodeId            = null,
                                           String?  VendorName        = null,
                                           String?  Model             = null,
                                           String?  SerialNumber      = null,
                                           String?  SoftwareVersion   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName            = "ocpp";

        /// <summary>
        /// What this gateway calls itself when nothing says otherwise.
        /// </summary>
        public const String  DefaultNodeId          = "gw001";

        /// <summary>
        /// The vendor reported when nothing says otherwise.
        /// </summary>
        public const String  DefaultVendorName      = "GraphDefined GmbH";

        /// <summary>
        /// The model reported when nothing says otherwise.
        /// </summary>
        public const String  DefaultModel           = "Gateway";

        /// <summary>
        /// The longest a networking node identification may be written. OCPP
        /// 2.1 allows 48 characters for an identifier of this kind.
        /// </summary>
        public const Int32   MaxNodeIdLength        = 48;

        /// <summary>
        /// The longest the vendor may be written; what OCPP 2.1 allows.
        /// </summary>
        public const Int32   MaxVendorNameLength    = 50;

        /// <summary>
        /// The longest a model may be written.
        /// </summary>
        public const Int32   MaxModelLength         = 20;

        /// <summary>
        /// The longest a serial number may be written.
        /// </summary>
        public const Int32   MaxSerialNumberLength  = 25;

        /// <summary>
        /// The longest a software version may be written.
        /// </summary>
        public const Int32   MaxVersionLength       = 50;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty

            => NodeId          is null &&
               VendorName      is null &&
               Model           is null &&
               SerialNumber    is null &&
               SoftwareVersion is null;

        #endregion


        #region (static) TryRead(Document, out Configuration, out Error)

        /// <summary>
        /// The "ocpp" section of the given configuration document - empty where
        /// the document has none - or the one sentence that says what is wrong
        /// with it.
        /// </summary>
        /// <param name="Document">The configuration file as the node below read it.</param>
        /// <param name="Configuration">The section.</param>
        /// <param name="Error">What is wrong with it.</param>
        public static Boolean TryRead(JObject                                      Document,
                                      [NotNullWhen(true)]  out OCPPConfiguration?  Configuration,
                                      [NotNullWhen(false)] out String?             Error)
        {

            Configuration  = null;
            Error          = null;

            switch (Document[SectionName])
            {

                case null:
                case JValue { Type: JTokenType.Null }:
                    Configuration = new OCPPConfiguration();
                    return true;

                case JObject section:
                    return TryParse(section, out Configuration, out Error);

                default:
                    Error = $"'{SectionName}' must be a JSON object.";
                    return false;

            }

        }

        #endregion

        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "ocpp" section, or the one sentence that says what is wrong with it.
        /// </summary>
        /// <remarks>
        /// A field it does not know is an error rather than one passed over:
        /// somebody who wrote another believed they had configured something.
        /// </remarks>
        public static Boolean TryParse(JObject                                      JSON,
                                       [NotNullWhen(true)]  out OCPPConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Configuration  = null;
            Error          = null;

            if (JSON.Properties().FirstOrDefault(property => property.Name is not ("nodeId" or "vendorName" or "model" or "serialNumber" or "softwareVersion")) is JProperty unknown)
            {
                Error = $"'{SectionName}.{unknown.Name}' is not known: the section has 'nodeId', 'vendorName', 'model', 'serialNumber' and 'softwareVersion'.";
                return false;
            }

            if (!ConfigurationReader.TryReadString(JSON, "nodeId",           SectionName, MaxNodeIdLength,       out var nodeId,          out Error) ||
                !ConfigurationReader.TryReadString(JSON, "vendorName",       SectionName, MaxVendorNameLength,   out var vendorName,      out Error) ||
                !ConfigurationReader.TryReadString(JSON, "model",            SectionName, MaxModelLength,        out var model,           out Error) ||
                !ConfigurationReader.TryReadString(JSON, "serialNumber",     SectionName, MaxSerialNumberLength, out var serialNumber,    out Error) ||
                !ConfigurationReader.TryReadString(JSON, "softwareVersion",  SectionName, MaxVersionLength,      out var softwareVersion, out Error))
            {
                return false;
            }

            // Whether the text is a networking node identification is decided
            // where one is made - this section only promises that the file said
            // something, and OCPP decides whether it can be addressed by it.
            if (nodeId is not null && nodeId.Any(Char.IsWhiteSpace))
            {
                Error = $"'{SectionName}.nodeId' must not contain spaces.";
                return false;
            }

            Configuration = new OCPPConfiguration(
                                nodeId,
                                vendorName,
                                model,
                                serialNumber,
                                softwareVersion
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this gateway was not
        /// told about is not written, so that the file keeps saying "the
        /// default decides" rather than freezing today's default.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (NodeId          is not null)  json.Add("nodeId",           NodeId);
            if (VendorName      is not null)  json.Add("vendorName",       VendorName);
            if (Model           is not null)  json.Add("model",            Model);
            if (SerialNumber    is not null)  json.Add("serialNumber",     SerialNumber);
            if (SoftwareVersion is not null)  json.Add("softwareVersion",  SoftwareVersion);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => String.Concat(
                   NodeId ?? DefaultNodeId,
                   " (", VendorName ?? DefaultVendorName, " ", Model ?? DefaultModel, ")"
               );

        #endregion

    }

}
