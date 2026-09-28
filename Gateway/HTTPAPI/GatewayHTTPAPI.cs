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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": what every
    /// node has - see <see cref="NodeHTTPAPI"/> - and, once there is any, what
    /// only a gateway has.
    /// </summary>
    /// <remarks>
    /// The sign-in, the status and the clock, the configuration, name
    /// resolution and the time servers, the certificate store, the log and the
    /// event stream are the node's, as they are the vehicle's and the local
    /// controller's; this class used to have its own copy of all of them. A
    /// gateway adds no route of its own yet - what it is for, handing frames
    /// on between charging stations and whatever they talk to, is not here -
    /// and says nothing beyond what every node says: its status is every
    /// node's, its clock and its log need a sign-in and nothing more, and its
    /// certificate store keeps nothing a gateway would be using.
    /// </remarks>
    public class GatewayHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The gateway this API speaks for.
        /// </summary>
        public Gateway  Gateway  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Gateway">The gateway this API speaks for.</param>
        /// <param name="ExtAPI">The accounts and the groups they are in.</param>
        /// <param name="Log">Everything that happens inside this gateway.</param>
        /// <param name="APIPath">The root path of the API, the gateway's own by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public GatewayHTTPAPI(HTTPServer  HTTPServer,
                              Gateway     Gateway,
                              HTTPExtAPI  ExtAPI,
                              EventLog    Log,
                              HTTPPath?   APIPath   = null,
                              String?     Version   = null)

            : base(HTTPServer,
                   Gateway,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(GatewayHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.Gateway = Gateway;

        }

        #endregion

    }

}
