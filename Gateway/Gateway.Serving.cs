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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.OCPP.Node.Serving;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// What a gateway says of itself to the station server it shares with a
    /// local controller: where it listens when nothing says otherwise, and
    /// which names a CSMS that connects to it may not come under.
    /// </summary>
    /// <remarks>
    /// Everything else is OCPPServingNode's: the section "webSocketServer" and what
    /// it may say, the charging stations that may sign in and their login
    /// groups, the chains a station's certificate may lead to, the keys and
    /// certificates the port presents, OCPP-over-SSH, and a CSMS that connects to the
    /// gateway on the same port as a connection the other end makes - which a
    /// gateway lets in whenever one is written down, having no switch of its
    /// own for reporting to a CSMS at all.
    /// </remarks>
    public partial class Gateway
    {

        #region Data

        /// <summary>
        /// The TCP port the charging stations and local controllers below a
        /// gateway connect to, unless its section "webSocketServer" says another.
        /// </summary>
        /// <remarks>
        /// Beside the gateway's web interface (2353), as a local controller's
        /// station port (2351) is beside its own (2350) - and not the local
        /// controller's, so that both run on one bench.
        /// </remarks>
        public static readonly IPPort  DefaultStationServerPort  = IPPort.Parse(2354);

        #endregion


        #region (private) BuildStationServer()

        /// <summary>
        /// The section "webSocketServer" of the document the node below has already
        /// read, at the gateway's own port where it names none, and the server
        /// built with it. Nothing listens yet.
        /// </summary>
        /// <remarks>
        /// A section that is wrong stops the gateway here, as one of a local
        /// controller stops it: somebody wrote down how stations reach it and
        /// got it wrong, and quietly listening somewhere else would be worse.
        /// </remarks>
        private void BuildStationServer()
        {

            WebSocketServerConfiguration? section = null;

            switch (ConfigurationDocument[WebSocketServerConfiguration.SectionName])
            {

                case null:
                case JValue { Type: JTokenType.Null }:
                    break;

                case JObject json:
                    if (!WebSocketServerConfiguration.TryParse(json, out section, out var problem, GatewayKind.Noun))
                        throw new InvalidOperationException($"'{ConfigFile.Path}': {problem} Repair or remove '{ConfigFile.Path}' and start again.");
                    break;

                default:
                    throw new InvalidOperationException($"'{ConfigFile.Path}': '{WebSocketServerConfiguration.SectionName}' must be a JSON object. " +
                                                        $"Repair or remove '{ConfigFile.Path}' and start again.");

            }

            section ??= new WebSocketServerConfiguration();

            // The gateway's port where the file names none - not written to the
            // file, which goes on saying "the default decides".
            BuildOCPPServer(section with { TCPPort = section.TCPPort ?? DefaultStationServerPort });

        }

        #endregion

    }

}
